# OTEL Collector (contrib build, same version as docker-compose) exporting to Application Insights.
# Apps send OTLP/HTTP to http://otel-collector (internal ingress, port 80 -> 4318). The Application
# Map in Application Insights is the cloud version of Day 13's Jaeger waterfall.

resource "azurerm_container_app" "otel" {
  name                         = "otel-collector"
  resource_group_name          = azurerm_resource_group.session.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.tags

  secret {
    name  = "appinsights-cs"
    value = azurerm_application_insights.appi.connection_string
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "otel-collector"
      image  = "docker.io/otel/opentelemetry-collector-contrib:0.115.1"
      cpu    = 0.5
      memory = "1Gi"
      args   = ["--config=env:OTEL_CONFIG"]

      env {
        name  = "OTEL_CONFIG"
        value = file("${path.module}/otel-collector-config.yaml")
      }
      env {
        name        = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        secret_name = "appinsights-cs"
      }
    }
  }

  ingress {
    external_enabled           = false
    transport                  = "http"
    target_port                = 4318
    allow_insecure_connections = true
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}
