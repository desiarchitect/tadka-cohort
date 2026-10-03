# Core: resource group, observability, network, Container Apps environment.

locals {
  ha = var.mode == "ha"

  tags = {
    project = "tadka"
    purpose = "cohort-live-session"
    mode    = var.mode
    adr     = "064"
  }
}

# Globally-unique names (Postgres, Front Door endpoint, WAF policy) need a suffix.
resource "random_string" "suffix" {
  length  = 6
  upper   = false
  special = false
}

resource "azurerm_resource_group" "session" {
  name     = var.resource_group_name
  location = var.location
  tags     = local.tags
}

# ── Observability ────────────────────────────────────────────────────────────────────────────────
# Log Analytics is required by the Container Apps environment (container stdout = our Serilog JSON).
# Application Insights receives traces/metrics from the OTEL Collector: the cloud version of Jaeger.
resource "azurerm_log_analytics_workspace" "logs" {
  name                = "log-tadka-${random_string.suffix.result}"
  location            = azurerm_resource_group.session.location
  resource_group_name = azurerm_resource_group.session.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

resource "azurerm_application_insights" "appi" {
  name                = "appi-tadka-${random_string.suffix.result}"
  location            = azurerm_resource_group.session.location
  resource_group_name = azurerm_resource_group.session.name
  workspace_id        = azurerm_log_analytics_workspace.logs.id
  application_type    = "web"
  tags                = local.tags
}

# ── Network ──────────────────────────────────────────────────────────────────────────────────────
# A VNet-integrated environment is needed only because Kafka and Redis are container apps with TCP
# ingress (TCP ingress requires a custom VNet). 16 Kafka topics > Event Hubs Standard's 10 per namespace,
# so Kafka runs as a container here (ADR-064). The same VNet also gives Postgres private access below:
# the data tier has no public IP (Day 10).
resource "azurerm_virtual_network" "vnet" {
  name                = "vnet-tadka"
  location            = azurerm_resource_group.session.location
  resource_group_name = azurerm_resource_group.session.name
  address_space       = ["10.40.0.0/16"]
  tags                = local.tags
}

resource "azurerm_subnet" "aca" {
  name                 = "snet-container-apps"
  resource_group_name  = azurerm_resource_group.session.name
  virtual_network_name = azurerm_virtual_network.vnet.name
  address_prefixes     = ["10.40.0.0/23"]

  delegation {
    name = "container-apps"
    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

# Postgres private access (VNet integration). The subnet is delegated to Flexible Server and holds nothing
# else; the servers get private IPs here and NO public endpoint. Apps reach them from the ACA subnet.
resource "azurerm_subnet" "postgres" {
  name                 = "snet-postgres"
  resource_group_name  = azurerm_resource_group.session.name
  virtual_network_name = azurerm_virtual_network.vnet.name
  address_prefixes     = ["10.40.2.0/24"]
  service_endpoints    = ["Microsoft.Storage"]

  delegation {
    name = "postgres-flexible"
    service_delegation {
      name    = "Microsoft.DBforPostgreSQL/flexibleServers"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

# The server FQDN (psql-tadka-xxx.postgres.database.azure.com) CNAMEs into this zone; inside the linked
# VNet it resolves to the private IP. Connection strings keep using the FQDN. The provider requires the
# zone name to end in .postgres.database.azure.com.
resource "azurerm_private_dns_zone" "postgres" {
  name                = "tadka-${random_string.suffix.result}.private.postgres.database.azure.com"
  resource_group_name = azurerm_resource_group.session.name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "postgres" {
  name                  = "vnet-tadka-postgres"
  private_dns_zone_name = azurerm_private_dns_zone.postgres.name
  resource_group_name   = azurerm_resource_group.session.name
  virtual_network_id    = azurerm_virtual_network.vnet.id
  registration_enabled  = false
  tags                  = local.tags
}

# ── Container Apps environment = our load balancer + autoscaler ─────────────────────────────────
# Built-in Envoy ingress does the L7 load balancing; there is no separate LB resource (contrast: ALB on AWS).
resource "azurerm_container_app_environment" "env" {
  name                           = "cae-tadka"
  location                       = azurerm_resource_group.session.location
  resource_group_name            = azurerm_resource_group.session.name
  log_analytics_workspace_id     = azurerm_log_analytics_workspace.logs.id
  infrastructure_subnet_id       = azurerm_subnet.aca.id
  internal_load_balancer_enabled = false # the gateway's external ingress must be reachable from Front Door

  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }

  tags = local.tags

  lifecycle {
    # Azure fills this in for workload-profile environments; don't fight it on re-apply.
    ignore_changes = [infrastructure_resource_group_name]
  }
}
