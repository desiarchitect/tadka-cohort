# Redis. Only the menu cache (ADR-018), the SSE backplane (ADR-020), rate-limit counters (ADR-049) and
# rider geo (ADR-034) use it, so losing its data is fine: no persistence anywhere.
#
#   basic: one Redis container app (the cheap, honest choice).
#   ha:    Redis primary + replica + 3 Sentinels as container apps (the plan's documented fallback).
#          Why not Azure Managed Redis: the azurerm provider has azurerm_managed_redis, but Managed Redis
#          exposes no user-triggered failover (Microsoft's failover doc lists only patching, scaling and
#          hardware faults), so the Day-14 "fail it over on cue" demo is impossible. Sentinel also makes
#          the election visible: quorum 2 of 3 votes, promotes the replica, clients follow.
#          The older Azure Cache for Redis stops new-cache creation from 1 Oct 2026, so not that either.
#
# TCP ingress on a Container App needs an exposed port that is unique in the environment, so each Redis
# node and Sentinel gets its own port. Nodes ANNOUNCE "<app-name>:<exposed-port>" (hostnames, Redis 7)
# so Sentinel and clients reach them through the environment's internal load balancer, never by pod IP.

locals {
  redis_image = "docker.io/library/redis:7.2-alpine"

  # name => exposed TCP port (container listens on 6379 / 26379)
  redis_nodes = { "redis-a" = 6379, "redis-b" = 6380 }
  sentinels   = { "sentinel-1" = 26379, "sentinel-2" = 26380, "sentinel-3" = 26381 }

  redis_connection_string = local.ha ? join(",", concat(
    [for name, port in local.sentinels : "${name}:${port}"],
    ["serviceName=tadka", "abortConnect=false"]
  )) : "redis:6379,abortConnect=false"
}

# ── basic: single Redis ──────────────────────────────────────────────────────────────────────────
resource "azurerm_container_app" "redis" {
  count                        = local.ha ? 0 : 1
  name                         = "redis"
  resource_group_name          = azurerm_resource_group.session.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.tags

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "redis"
      image  = local.redis_image
      cpu    = 0.25
      memory = "0.5Gi"
      # --save "" (no RDB snapshots) cannot be an args element: the azurerm provider turns an empty string
      # into nil and the apply dies with "interface conversion: interface {} is nil, not string". It goes
      # through sh instead, like the Sentinels below.
      command = ["sh", "-c"]
      args    = ["exec redis-server --save '' --appendonly no"]
    }
  }

  ingress {
    external_enabled = false
    transport        = "tcp"
    target_port      = 6379
    exposed_port     = 6379
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

# ── ha: primary (redis-a) + replica (redis-b) ────────────────────────────────────────────────────
resource "azurerm_container_app" "redis_node" {
  for_each                     = local.ha ? local.redis_nodes : {}
  name                         = each.key
  resource_group_name          = azurerm_resource_group.session.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.tags

  template {
    min_replicas = 1
    max_replicas = 1 # a Redis node is a stateful singleton; never scale it

    container {
      name   = "redis"
      image  = local.redis_image
      cpu    = 0.25
      memory = "0.5Gi"
      # Through sh because --save "" cannot be an args element (see the single-Redis app above).
      command = ["sh", "-c"]
      args = [join(" ", concat(
        ["exec redis-server --port 6379 --save '' --appendonly no",
        "--replica-announce-ip", each.key, "--replica-announce-port", tostring(each.value)],
        # redis-b starts as a replica of redis-a; after a failover Sentinel rewrites roles at runtime.
        each.key == "redis-b" ? ["--replicaof", "redis-a", "6379"] : []
      ))]
    }
  }

  ingress {
    external_enabled = false
    transport        = "tcp"
    target_port      = 6379
    exposed_port     = each.value
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

# ── ha: three Sentinels (quorum 2) ───────────────────────────────────────────────────────────────
resource "azurerm_container_app" "sentinel" {
  for_each                     = local.ha ? local.sentinels : {}
  name                         = each.key
  resource_group_name          = azurerm_resource_group.session.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.tags

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name    = "sentinel"
      image   = local.redis_image
      cpu     = 0.25
      memory  = "0.5Gi"
      command = ["sh", "-c"]
      # Sentinel rewrites its config file, so it is generated into a writable path at start.
      args = [<<-EOT
        cat > /tmp/sentinel.conf <<CONF
        port 26379
        sentinel resolve-hostnames yes
        sentinel announce-hostnames yes
        sentinel announce-ip ${each.key}
        sentinel announce-port ${each.value}
        sentinel monitor tadka redis-a 6379 2
        sentinel down-after-milliseconds tadka 5000
        sentinel failover-timeout tadka 15000
        sentinel parallel-syncs tadka 1
        CONF
        exec redis-server /tmp/sentinel.conf --sentinel
      EOT
      ]
    }
  }

  ingress {
    external_enabled = false
    transport        = "tcp"
    target_port      = 26379
    exposed_port     = each.value
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  depends_on = [azurerm_container_app.redis_node]
}
