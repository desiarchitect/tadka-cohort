variable "subscription_id" {
  description = "Azure subscription id. Leave null to use ARM_SUBSCRIPTION_ID (cloud-up.ps1 sets it)."
  type        = string
  default     = null
}

variable "mode" {
  description = "basic = cheapest full system (Day 12, Day 16). ha = zone-redundant Postgres + read replica + Redis Sentinel (Day 14 failover)."
  type        = string
  default     = "basic"

  validation {
    condition     = contains(["basic", "ha"], var.mode)
    error_message = "mode must be \"basic\" or \"ha\"."
  }
}

variable "location" {
  description = "Azure region. Central India (Pune) is closest to the cohort."
  type        = string
  default     = "centralindia"
}

variable "resource_group_name" {
  description = "The ONE resource group for the session. cloud-down destroys it."
  type        = string
  default     = "rg-tadka-session"
}

variable "image_prefix" {
  description = "Image name prefix; the service name is appended (ghcr.io/desiarchitect/tadka-cohort-api, ...). Built by .github/workflows/images.yml; the packages are public."
  type        = string
  default     = "ghcr.io/desiarchitect/tadka-cohort"
}

variable "image_tag" {
  description = "Image tag to deploy: a git sha for a pinned session, or latest."
  type        = string
  default     = "latest"
}

variable "ghcr_username" {
  description = "Only needed if the GHCR packages are private. Leave empty when they are public."
  type        = string
  default     = ""
}

variable "ghcr_token" {
  description = "GitHub PAT with read:packages, only if the GHCR packages are private."
  type        = string
  default     = ""
  sensitive   = true
}

variable "max_replicas" {
  description = "Autoscale ceiling for the gateway and Api (HTTP concurrency rule). Keeps k6 stress runs inside the free grant."
  type        = number
  default     = 5
}

variable "http_concurrency_per_replica" {
  description = "Concurrent requests per replica before Container Apps adds another replica."
  type        = number
  default     = 30
}

variable "db_retry_enabled" {
  description = "Database:EnableRetryOnFailure for all 4 services (ADR-064). Day 14 starts with false (see the failure), then flips to true (see the fix)."
  type        = bool
  default     = false
}

variable "waf_rate_limit_per_minute" {
  description = "Front Door WAF: requests per minute per client IP before a Block. The per-client limit now lives at the edge (see ADR-064)."
  type        = number
  default     = 3000
}

variable "budget_amount" {
  description = "Monthly budget alert on the session resource group, in the billing account's currency (INR for an India-billed account)."
  type        = number
  default     = 1000
}

variable "enable_front_door" {
  description = "true = Azure Front Door Standard (CDN + WAF + TLS + origin lock) in front of the gateway. false (cloud-up.ps1 -NoFrontDoor) = the gateway is the public entry point. Needed because Azure refuses Front Door on Free Trial and Student subscriptions."
  type        = bool
  default     = true
}
variable "load_test_mode" {
  description = "Day 16 only (cloud-up.ps1 -LoadTest). false keeps the WAF at waf_rate_limit_per_minute so the room first SEES the edge block its own k6 run. true raises the threshold to waf_load_test_rate_limit_per_minute."
  type        = bool
  default     = false
}

variable "waf_load_test_rate_limit_per_minute" {
  description = "WAF per-IP threshold while load_test_mode is on. k6 stress.js from one laptop peaks around 150-300 rps (9,000-18,000/min); 60000/min (1,000 rps) leaves 3x headroom. The rule stays, just higher: an allow-listed load test, not a disabled edge."
  type        = number
  default     = 60000
}

variable "kafka_consumer_scaling" {
  description = "Optional, default off. true = Payment scales 1..4 on Kafka consumer lag (KEDA kafka scaler) and auto-created topics get 3 partitions, so the room sees 3 busy Payment consumers and a 4th idle one (Day 9: partitions cap consumer parallelism)."
  type        = bool
  default     = false
}

variable "enable_managed_redis" {
  description = "ha mode only: also deploy Azure Managed Redis (HA on) next to Sentinel, for comparison. The apps keep using Sentinel. Adds hourly cost for the Day 14 session."
  type        = bool
  default     = false
}

variable "managed_redis_sku" {
  description = "Smallest Managed Redis SKU for the comparison. Confirm at the first dry run that this SKU accepts high_availability_enabled = true."
  type        = string
  default     = "Balanced_B0"
}

variable "budget_alert_emails" {
  description = "Who gets the budget alert emails. Required: a forgotten teardown must reach a human."
  type        = list(string)
}
