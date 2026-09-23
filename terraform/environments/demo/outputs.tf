output "cloudfront_url" {
  description = "Client-facing entry (CDN + TLS). Azure equivalent: the Front Door URL."
  value       = "https://${module.cdn.domain_name}"
}

output "alb_dns_name" {
  description = "The ALB behind CloudFront (compare to local YARP :8080)."
  value       = module.edge.alb_dns_name
}

output "ecr_repositories" {
  value = module.registry.repository_urls
}

output "redis_endpoint" {
  value = module.cache.redis_endpoint
}

output "kafka_bootstrap" {
  value = local.kafka_bootstrap
}

output "ordering_db" {
  value = module.monolith.db_endpoint
}

output "cost_note" {
  value = "Modeled, not a bill: ~Rs 31,300/month for this 'built' shape with 4 RDS + MSK (docs/cost-model.md); less with enable_msk=false. Never applied in the cohort."
}
