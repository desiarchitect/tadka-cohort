# Postgres Flexible Server: ONE server, FOUR databases (database-per-service at the logical level).
# Deliberate change from local (4 containers): 4 servers would be 4 bills. Local compose is unchanged.
#
#   basic: Burstable B1ms, no HA, no replica (B1ms supports neither). Read string = primary.
#   ha:    General Purpose D2ds_v5 + zone-redundant HA standby (zone 2) + one async read replica.
#          TadkaReadDbContext (Day 5 read/write split, ADR-016) points at the replica.
#
# Both modes: private access only (delegated subnet + private DNS zone in main.tf). Nothing on the
# internet, including the instructor's laptop, can open a connection. Debug through the apps' logs.

locals {
  pg_admin     = "tadka"
  pg_sku       = local.ha ? "GP_Standard_D2ds_v5" : "B_Standard_B1ms"
  pg_databases = ["tadka", "tadka_payment", "tadka_delivery", "tadka_restaurant"]
}

# No ; or = in the password: it is embedded in Npgsql connection strings.
resource "random_password" "db" {
  length           = 32
  special          = true
  override_special = "-_"
  min_upper        = 2
  min_lower        = 2
  min_numeric      = 2
  min_special      = 1
}

resource "azurerm_postgresql_flexible_server" "main" {
  name                   = "psql-tadka-${random_string.suffix.result}"
  resource_group_name    = azurerm_resource_group.session.name
  location               = azurerm_resource_group.session.location
  version                = "16"
  administrator_login    = local.pg_admin
  administrator_password = random_password.db.result
  sku_name               = local.pg_sku
  storage_mb             = 32768
  backup_retention_days  = 7
  zone                   = "1"

  # Private access (VNet integration, main.tf): a private IP in snet-postgres, no public endpoint, no
  # firewall rules. The old "allow Azure services" (0.0.0.0) rule let ANY Azure customer's resources try
  # to connect; the data tier is never public (Day 10). Provisioning is a few minutes slower: measure it.
  delegated_subnet_id           = azurerm_subnet.postgres.id
  private_dns_zone_id           = azurerm_private_dns_zone.postgres.id
  public_network_access_enabled = false

  dynamic "high_availability" {
    for_each = local.ha ? [1] : []
    content {
      mode                      = "ZoneRedundant"
      standby_availability_zone = "2"
    }
  }

  tags = local.tags

  # The zone must be linked to the VNet before the server registers its A record in it.
  depends_on = [azurerm_private_dns_zone_virtual_network_link.postgres]

  lifecycle {
    # After a forced failover the primary lives in the standby's zone; don't "fix" it back on re-apply.
    ignore_changes = [zone, high_availability[0].standby_availability_zone]
  }
}

resource "azurerm_postgresql_flexible_server_database" "db" {
  for_each  = toset(local.pg_databases)
  name      = each.value
  server_id = azurerm_postgresql_flexible_server.main.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# ha only: async read replica. "Replica = scale reads + lag. Standby = survive failure + no reads."
resource "azurerm_postgresql_flexible_server" "replica" {
  count               = local.ha ? 1 : 0
  name                = "psql-tadka-${random_string.suffix.result}-replica"
  resource_group_name = azurerm_resource_group.session.name
  location            = azurerm_resource_group.session.location
  create_mode         = "Replica"
  source_server_id    = azurerm_postgresql_flexible_server.main.id
  version             = "16"
  sku_name            = local.pg_sku
  storage_mb          = 32768
  zone                = "3"

  # Same private access as the primary: same delegated subnet, same private DNS zone, no public endpoint.
  # (A replica of a VNet-integrated server must sit in a VNet it can reach; the same subnet is simplest.)
  delegated_subnet_id           = azurerm_subnet.postgres.id
  private_dns_zone_id           = azurerm_private_dns_zone.postgres.id
  public_network_access_enabled = false

  tags = local.tags

  # The replica copies the databases once they exist (migrations run from the apps after this).
  depends_on = [azurerm_postgresql_flexible_server_database.db]

  lifecycle {
    ignore_changes = [zone]
  }
}

locals {
  pg_host      = azurerm_postgresql_flexible_server.main.fqdn
  pg_read_host = local.ha ? azurerm_postgresql_flexible_server.replica[0].fqdn : local.pg_host
  pg_auth      = "Port=5432;Username=${local.pg_admin};Password=${random_password.db.result};SSL Mode=Require"

  # Pool sizes are MUCH smaller than local (50), because ADR-015 math changes in the cloud: B1ms allows
  # ~50 connections for the whole server, shared by 4 databases and up to var.max_replicas (5) Api
  # replicas. Worst case: 5 x (4 + 3) + 3 x 3 = 44. Scale the Api past 5 and you meet
  # "too many connections" before you meet CPU (a Day-16 talking point). GP D2ds_v5 (ha) allows far more.
  connection_strings = {
    tadka      = "Host=${local.pg_host};Database=tadka;${local.pg_auth};Minimum Pool Size=1;Maximum Pool Size=4"
    tadka_read = "Host=${local.pg_read_host};Database=tadka;${local.pg_auth};Maximum Pool Size=3"
    # With kafka_consumer_scaling Payment runs up to 4 replicas: pool 2 keeps the worst case at
    # 5 x (4 + 3) + 4 x 2 + 2 x 3 = 49. Still tight on B1ms: confirm on the dry run.
    payment    = "Host=${local.pg_host};Database=tadka_payment;${local.pg_auth};Maximum Pool Size=${var.kafka_consumer_scaling ? 2 : 3}"
    delivery   = "Host=${local.pg_host};Database=tadka_delivery;${local.pg_auth};Maximum Pool Size=3"
    restaurant = "Host=${local.pg_host};Database=tadka_restaurant;${local.pg_auth};Maximum Pool Size=3"
  }
}
