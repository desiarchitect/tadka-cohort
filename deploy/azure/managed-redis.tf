# Optional: Azure Managed Redis, deployed NEXT TO the Sentinel topology for a side-by-side comparison
# on Day 14 (ADR-064). The apps keep using Sentinel: Managed Redis has no user-triggered failover, so it
# cannot carry the live "kill the primary" demo. What it shows instead is the production answer: the same
# primary + replica election, zone redundant, run by Azure, with no Sentinels to operate.
#
# Only created when mode = "ha" AND enable_managed_redis = true, so a Day 14 session without the switch,
# and every basic session, pays nothing for it.

resource "azurerm_managed_redis" "compare" {
  count = local.ha && var.enable_managed_redis ? 1 : 0

  name                      = "amr-tadka-${random_string.suffix.result}"
  resource_group_name       = azurerm_resource_group.session.name
  location                  = azurerm_resource_group.session.location
  sku_name                  = var.managed_redis_sku
  high_availability_enabled = true
  tags                      = local.tags

  default_database {
    access_keys_authentication_enabled = true
    client_protocol                    = "Encrypted"
    eviction_policy                    = "VolatileLRU"
  }
}
