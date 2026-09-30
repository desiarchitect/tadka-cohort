# The 4 services + the gateway. Same images as CI builds; config comes ONLY from env vars, which is how
# every setting in appsettings.json is overridden (ConnectionStrings__X, Kafka__BootstrapServers,
# Jwt__SigningKeyPem, ReverseProxy__Clusters__...__Address). No app code knows it is in Azure.
#
# Only the gateway has external ingress. The other four are internal-only: reachable as http://<name>
# inside the environment, unreachable from the internet. Each service still validates the JWT itself
# (ADR-031): the gateway is an edge, not a trust boundary.

# ── Secrets (Container App secrets, generated here; Key Vault is the production upgrade) ────────────
# RS256 signing key (ADR-067). ONLY the monolith holds the private key (Jwt__SigningKeyPem); it is one key shared
# by every replica, so a token signed by replica A verifies on replica B. Payment, Delivery and Restaurant hold no
# signing secret at all: they fetch the PUBLIC key from the monolith's JWKS endpoint (Jwt__JwksBaseUrl).
resource "tls_private_key" "jwt" {
  algorithm = "RSA"
  rsa_bits  = 2048
}

# 32 random bytes, base64: the AES key for field-level PII encryption (ADR-052). Without it the monolith
# refuses to boot outside Development (Demo:EncryptPiiAtRest defaults to true).
resource "random_bytes" "pii_key" {
  length = 32
}

locals {
  secret_values = {
    "jwt-signing-key" = tls_private_key.jwt.private_key_pem
    "pii-key"         = random_bytes.pii_key.base64
    "cs-tadka"        = local.connection_strings.tadka
    "cs-tadka-read"   = local.connection_strings.tadka_read
    "cs-payment"      = local.connection_strings.payment
    "cs-delivery"     = local.connection_strings.delivery
    "cs-restaurant"   = local.connection_strings.restaurant
    "cs-redis"        = local.redis_connection_string
  }

  use_registry_credentials = var.ghcr_token != ""

  # Where the other services fetch the monolith's PUBLIC signing keys (internal ingress, ADR-067).
  jwks_base_url = "http://api"

  # Settings every .NET app gets.
  common_env = {
    "ASPNETCORE_ENVIRONMENT"         = "Production"
    "OTEL_EXPORTER_OTLP_ENDPOINT"    = "http://otel-collector"
    "OTEL_EXPORTER_OTLP_PROTOCOL"    = "http/protobuf"
    "Database__EnableRetryOnFailure" = tostring(var.db_retry_enabled)
    "Kafka__BootstrapServers"        = "kafka:9092"
  }

  # Behind Front Door + Envoy + YARP every request arrives from a proxy IP, so the per-IP limiters in the
  # gateway (in-memory) and the monolith (Redis, ADR-049) would treat ALL users as ONE client. Raise them
  # out of the way; the per-client limit lives in the Front Door WAF rule instead (ADR-064).
  proxy_safe_rate_limit = "100000"

  apps = {
    api = {
      image      = "${var.image_prefix}-api:${var.image_tag}"
      external   = false
      ready_path = "/health/ready"
      scale_http = true
      env = {
        "RateLimit__PerMinute"          = local.proxy_safe_rate_limit
        "Services__Restaurant__BaseUrl" = "http://restaurant"
      }
      secret_env = {
        "ConnectionStrings__TadkaDb"        = "cs-tadka"
        "ConnectionStrings__TadkaDbReplica" = "cs-tadka-read"
        "ConnectionStrings__Redis"          = "cs-redis"
        "Jwt__SigningKeyPem"                = "jwt-signing-key"
        "Demo__EncryptionKey"               = "pii-key"
      }
    }
    payment = {
      image       = "${var.image_prefix}-payment:${var.image_tag}"
      external    = false
      ready_path  = "/health/ready"
      scale_http  = false
      scale_kafka = var.kafka_consumer_scaling # optional KEDA lag rule, see below
      env         = { "Jwt__JwksBaseUrl" = local.jwks_base_url }
      secret_env = {
        "ConnectionStrings__PaymentDb" = "cs-payment"
      }
    }
    delivery = {
      image      = "${var.image_prefix}-delivery:${var.image_tag}"
      external   = false
      ready_path = "/health/ready"
      scale_http = false
      env        = { "Jwt__JwksBaseUrl" = local.jwks_base_url }
      secret_env = {
        "ConnectionStrings__DeliveryDb" = "cs-delivery"
        "ConnectionStrings__Redis"      = "cs-redis"
      }
    }
    restaurant = {
      image      = "${var.image_prefix}-restaurant:${var.image_tag}"
      external   = false
      ready_path = "/health/ready"
      scale_http = false
      env        = { "Jwt__JwksBaseUrl" = local.jwks_base_url }
      secret_env = {
        "ConnectionStrings__RestaurantDb" = "cs-restaurant"
        "ConnectionStrings__Redis"        = "cs-redis"
      }
    }
  }

  # Optional Kafka consumer scaling (var.kafka_consumer_scaling). Payment is the order-placed consumer
  # (group tadka-payment). KAFKA_NUM_PARTITIONS=3 in kafka.tf gives order-placed 3 partitions.
  # allowIdleConsumers=true lets KEDA go PAST the partition count, so replica 4 exists and sits idle:
  # "partitions cap consumer parallelism", visible live. (Default false would cap KEDA at 3 and hide it.)
  kafka_scaling_max_replicas = 4
  payment_kafka_scale_metadata = {
    bootstrapServers   = "kafka:9092"
    consumerGroup      = "tadka-payment"
    topic              = "order-placed"
    lagThreshold       = "5"
    allowIdleConsumers = "true"
  }

  # YARP destinations -> internal service names (appsettings.json points at localhost ports).
  gateway_env = {
    # Origin lockdown (frontdoor.tf): 403 unless X-Azure-FDID matches this profile. Empty = off (local).
    "Gateway__RequiredFrontDoorId"                                      = azurerm_cdn_frontdoor_profile.fd.resource_guid
    "Gateway__RateLimitPerMinute"                                       = local.proxy_safe_rate_limit
    "ReverseProxy__Clusters__monolith__Destinations__d1__Address"       = "http://api/"
    "ReverseProxy__Clusters__payment__Destinations__d1__Address"        = "http://payment/"
    "ReverseProxy__Clusters__delivery__Destinations__d1__Address"       = "http://delivery/"
    "ReverseProxy__Clusters__restaurant__Destinations__stable__Address" = "http://restaurant/"
    # No canary deployment in the cloud; point it at stable so the WeightedCanary policy can never pick
    # a dead address (Canary:RestaurantPercent defaults to 0 anyway).
    "ReverseProxy__Clusters__restaurant__Destinations__canary__Address" = "http://restaurant/"
  }
}

# ── The 4 services ───────────────────────────────────────────────────────────────────────────────
resource "azurerm_container_app" "svc" {
  for_each                     = local.apps
  name                         = each.key
  resource_group_name          = azurerm_resource_group.session.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.tags

  dynamic "secret" {
    for_each = toset(distinct(values(each.value.secret_env)))
    content {
      name  = secret.value
      value = local.secret_values[secret.value]
    }
  }

  dynamic "secret" {
    for_each = local.use_registry_credentials ? ["ghcr-token"] : []
    content {
      name  = secret.value
      value = var.ghcr_token
    }
  }

  dynamic "registry" {
    for_each = local.use_registry_credentials ? [1] : []
    content {
      server               = "ghcr.io"
      username             = var.ghcr_username
      password_secret_name = "ghcr-token"
    }
  }

  template {
    # min 1 everywhere: each service runs Database.Migrate() on startup, and the first revision must
    # migrate ALONE before anything scales out (migration race, ADR-064). Also no cold start in class.
    # Payment with kafka_consumer_scaling: extra replicas only start once the first one has migrated and
    # lag has built up, and Migrate() on an up-to-date database is a no-op, so scale-out does not race.
    min_replicas = 1
    max_replicas = each.value.scale_http ? var.max_replicas : (try(each.value.scale_kafka, false) ? local.kafka_scaling_max_replicas : 1)

    dynamic "http_scale_rule" {
      for_each = each.value.scale_http ? [1] : []
      content {
        name                = "http-concurrency"
        concurrent_requests = tostring(var.http_concurrency_per_replica)
      }
    }

    # KEDA kafka scaler: replicas follow consumer-group lag on order-placed (plaintext broker, no auth).
    dynamic "custom_scale_rule" {
      for_each = try(each.value.scale_kafka, false) ? [1] : []
      content {
        name             = "kafka-lag"
        custom_rule_type = "kafka"
        metadata         = local.payment_kafka_scale_metadata
      }
    }

    container {
      name   = each.key
      image  = each.value.image
      cpu    = 0.5
      memory = "1Gi"

      dynamic "env" {
        for_each = merge(local.common_env, each.value.env)
        content {
          name  = env.key
          value = env.value
        }
      }

      dynamic "env" {
        for_each = each.value.secret_env
        content {
          name        = env.key
          secret_name = env.value
        }
      }

      # Startup covers migrations + seeding (up to ~100 s) before liveness takes over.
      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health"
        interval_seconds        = 10
        failure_count_threshold = 10
      }

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health"
      }

      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = each.value.ready_path
      }
    }
  }

  ingress {
    external_enabled           = false
    transport                  = "auto"
    target_port                = 8080
    allow_insecure_connections = true # plain http://<name> inside the environment
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  # Postgres is private now (no firewall rules): the databases, the replica (ha) and the private DNS
  # link must exist before the first revision migrates.
  depends_on = [
    azurerm_postgresql_flexible_server_database.db,
    azurerm_postgresql_flexible_server.replica,
    azurerm_private_dns_zone_virtual_network_link.postgres,
    azurerm_container_app.kafka,
    azurerm_container_app.redis,
    azurerm_container_app.sentinel,
    azurerm_container_app.otel,
  ]
}

# ── The gateway: the ONLY app with external ingress ─────────────────────────────────────────────
resource "azurerm_container_app" "gateway" {
  name                         = "gateway"
  resource_group_name          = azurerm_resource_group.session.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.tags

  dynamic "secret" {
    for_each = local.use_registry_credentials ? ["ghcr-token"] : []
    content {
      name  = secret.value
      value = var.ghcr_token
    }
  }

  dynamic "registry" {
    for_each = local.use_registry_credentials ? [1] : []
    content {
      server               = "ghcr.io"
      username             = var.ghcr_username
      password_secret_name = "ghcr-token"
    }
  }

  template {
    min_replicas = 1
    max_replicas = var.max_replicas

    http_scale_rule {
      name                = "http-concurrency"
      concurrent_requests = tostring(var.http_concurrency_per_replica)
    }

    container {
      name   = "gateway"
      image  = "${var.image_prefix}-gateway:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      dynamic "env" {
        for_each = merge(
          { for k, v in local.common_env : k => v if k != "Database__EnableRetryOnFailure" && k != "Kafka__BootstrapServers" },
          local.gateway_env
        )
        content {
          name  = env.key
          value = env.value
        }
      }

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health"
      }

      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/ready"
      }
    }
  }

  ingress {
    external_enabled = true # public HTTPS on *.azurecontainerapps.io; Front Door is the front door
    transport        = "auto"
    target_port      = 8080
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  depends_on = [azurerm_container_app.svc]
}
