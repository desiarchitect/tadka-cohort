# Azure Front Door Standard = CDN + TLS + WAF in front of the gateway.
# Default *.azurefd.net domain with a managed certificate: no custom domain, no DNS work.
#
# Caching: only public GET /api/v1/restaurants* (Day 6's menu read), with a short TTL. Everything else
# passes through uncached (orders, auth, SSE are per-user). Look for the `x-cache` response header:
# TCP_MISS on the first call, TCP_HIT on the next.
#
# WAF: Standard supports CUSTOM rules (the per-client-IP rate limit below). The Microsoft MANAGED rule
# sets (OWASP/DRS, bot protection) need Premium: that is the production upgrade, named not built.
#
# Origin lockdown (Standard tier): Front Door stamps every request it forwards with X-Azure-FDID = this
# profile's resource_guid. apps.tf passes it to the gateway as Gateway__RequiredFrontDoorId, and the
# gateway 403s requests without it, so nobody can skip the WAF/CDN by calling *.azurecontainerapps.io.
# Exempt: /health, /health/ready and the SSE path (realtime goes to the gateway URL on purpose, ADR-064).
# Private Link to the origin is the stronger Premium option: named, not built.
#
# Dependency direction (no cycle): profile -> gateway app (env var) -> origin (host_name). The profile
# itself depends only on the resource group.

resource "azurerm_cdn_frontdoor_profile" "fd" {
  name                = "afd-tadka"
  resource_group_name = azurerm_resource_group.session.name
  sku_name            = "Standard_AzureFrontDoor"
  tags                = local.tags
}

resource "azurerm_cdn_frontdoor_endpoint" "fd" {
  name                     = "tadka-${random_string.suffix.result}"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.fd.id
  tags                     = local.tags
}

resource "azurerm_cdn_frontdoor_origin_group" "gateway" {
  name                     = "gateway"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.fd.id
  session_affinity_enabled = false

  load_balancing {
    sample_size                 = 4
    successful_samples_required = 3
  }

  health_probe {
    path                = "/health"
    protocol            = "Https"
    request_type        = "GET"
    interval_in_seconds = 60
  }
}

resource "azurerm_cdn_frontdoor_origin" "gateway" {
  name                           = "gateway"
  cdn_frontdoor_origin_group_id  = azurerm_cdn_frontdoor_origin_group.gateway.id
  enabled                        = true
  host_name                      = azurerm_container_app.gateway.ingress[0].fqdn
  origin_host_header             = azurerm_container_app.gateway.ingress[0].fqdn
  http_port                      = 80
  https_port                     = 443
  certificate_name_check_enabled = true
  priority                       = 1
  weight                         = 1000
}

# Rule set: short-TTL cache for the public restaurant/menu reads only.
resource "azurerm_cdn_frontdoor_rule_set" "cache" {
  name                     = "tadkacache"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.fd.id
}

resource "azurerm_cdn_frontdoor_rule" "restaurants_short_ttl" {
  name                      = "CacheRestaurantReads"
  cdn_frontdoor_rule_set_id = azurerm_cdn_frontdoor_rule_set.cache.id
  order                     = 1
  behavior_on_match         = "Continue"

  conditions {
    request_method_condition {
      operator     = "Equal"
      match_values = ["GET"]
    }
    # Front Door matches the path without the leading slash; both forms listed to be safe.
    url_path_condition {
      operator     = "BeginsWith"
      match_values = ["api/v1/restaurants", "/api/v1/restaurants"]
      transforms   = ["Lowercase"]
    }
  }

  actions {
    route_configuration_override_action {
      cache_behavior                = "OverrideAlways"
      cache_duration                = "00:00:30"
      query_string_caching_behavior = "UseQueryString"
      compression_enabled           = true
    }
  }

  depends_on = [azurerm_cdn_frontdoor_origin_group.gateway, azurerm_cdn_frontdoor_origin.gateway]
}

resource "azurerm_cdn_frontdoor_route" "all" {
  name                          = "all-to-gateway"
  cdn_frontdoor_endpoint_id     = azurerm_cdn_frontdoor_endpoint.fd.id
  cdn_frontdoor_origin_group_id = azurerm_cdn_frontdoor_origin_group.gateway.id
  cdn_frontdoor_origin_ids      = [azurerm_cdn_frontdoor_origin.gateway.id]
  cdn_frontdoor_rule_set_ids    = [azurerm_cdn_frontdoor_rule_set.cache.id]
  enabled                       = true

  patterns_to_match      = ["/*"]
  supported_protocols    = ["Http", "Https"]
  https_redirect_enabled = true
  forwarding_protocol    = "HttpsOnly"
  link_to_default_domain = true
  # No route-level cache block: caching is OFF by default and switched on only by the rule above.
}

# ── WAF: per-client-IP rate limit (custom rule, Standard tier) ──────────────────────────────────────
resource "azurerm_cdn_frontdoor_firewall_policy" "waf" {
  name                = "tadkawaf${random_string.suffix.result}"
  resource_group_name = azurerm_resource_group.session.name
  sku_name            = azurerm_cdn_frontdoor_profile.fd.sku_name
  enabled             = true
  mode                = "Prevention"
  tags                = local.tags

  custom_rule {
    name                           = "RateLimitPerClientIp"
    enabled                        = true
    priority                       = 1
    type                           = "RateLimitRule"
    rate_limit_duration_in_minutes = 1
    # Default 3000/min: one laptop running k6 stress.js gets 403s (the edge protecting you from yourself).
    # cloud-up.ps1 -LoadTest (load_test_mode) raises it for the Day 16 run. A real load test runs from
    # many IPs or an allow-listed source; it never switches the edge off.
    rate_limit_threshold = var.load_test_mode ? var.waf_load_test_rate_limit_per_minute : var.waf_rate_limit_per_minute
    action               = "Block"

    match_condition {
      match_variable = "RequestUri"
      operator       = "Contains"
      match_values   = ["/"]
    }
  }
}

resource "azurerm_cdn_frontdoor_security_policy" "waf" {
  name                     = "tadka-waf"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.fd.id

  security_policies {
    firewall {
      cdn_frontdoor_firewall_policy_id = azurerm_cdn_frontdoor_firewall_policy.waf.id

      association {
        patterns_to_match = ["/*"]
        domain {
          cdn_frontdoor_domain_id = azurerm_cdn_frontdoor_endpoint.fd.id
        }
      }
    }
  }
}
