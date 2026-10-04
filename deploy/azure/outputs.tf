output "front_door_url" {
  description = "The public URL students hit (CDN + WAF + TLS)."
  value       = var.enable_front_door ? "https://${azurerm_cdn_frontdoor_endpoint.fd[0].host_name}" : ""
}

output "gateway_url" {
  description = "The gateway's own public ingress (Front Door's origin). Locked to Front Door (403 without X-Azure-FDID) except /health, /health/ready and the SSE path: this is the 'realtime' URL for live tracking."
  value       = "https://${azurerm_container_app.gateway.ingress[0].fqdn}"
}

output "front_door_id" {
  description = "X-Azure-FDID value the gateway requires (origin lockdown). Not a secret from the edge's point of view; it only proves the request came through OUR Front Door profile."
  value       = var.enable_front_door ? azurerm_cdn_frontdoor_profile.fd[0].resource_guid : ""
}

output "waf_rate_limit_per_minute" {
  description = "Effective WAF per-IP threshold for this session (raised by cloud-up.ps1 -LoadTest)."
  value       = var.load_test_mode ? var.waf_load_test_rate_limit_per_minute : var.waf_rate_limit_per_minute
}

output "kafka_consumer_scaling" {
  description = "true = Payment scales 1..4 on order-placed lag; topics auto-create with 3 partitions."
  value       = var.kafka_consumer_scaling
}

output "resource_group" {
  value = azurerm_resource_group.session.name
}

output "mode" {
  value = var.mode
}

output "postgres_server" {
  description = "Used by cloud-failover.ps1 (az postgres flexible-server restart --failover ...)."
  value       = azurerm_postgresql_flexible_server.main.name
}

output "postgres_replica" {
  value = local.ha ? azurerm_postgresql_flexible_server.replica[0].name : "none (basic mode: B1ms has no replicas)"
}

output "redis_topology" {
  value = local.ha ? "sentinel: redis-a (primary) + redis-b (replica) + sentinel-1..3, service name 'tadka'" : "single redis container"
}

output "managed_redis" {
  description = "Optional side-by-side Azure Managed Redis (ha + enable_managed_redis). The apps do not use it."
  value       = length(azurerm_managed_redis.compare) > 0 ? "${azurerm_managed_redis.compare[0].name} (${azurerm_managed_redis.compare[0].hostname})" : "not deployed"
}

output "container_apps" {
  description = "App names for `az containerapp replica list -g <rg> -n <name>` (watch autoscaling)."
  value = concat(
    ["gateway"],
    keys(local.apps),
    ["kafka", "otel-collector"],
    local.ha ? concat(keys(local.redis_nodes), keys(local.sentinels)) : ["redis"]
  )
}

output "application_insights" {
  value = azurerm_application_insights.appi.name
}

output "cost_reminder" {
  value = "This bills while it exists. Run scripts/cloud-down.ps1 after class. Record the real Cost Management figure in docs/cost-model.md."
}
