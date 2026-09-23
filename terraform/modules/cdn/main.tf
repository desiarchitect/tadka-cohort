# CloudFront in front of the ALB: TLS at the edge (default *.cloudfront.net certificate), a short-TTL
# cache for public GET /api/v1/restaurants* (Day 6's menu read), everything else passed through uncached.
# Azure equivalent: Front Door Standard (deploy/azure/frontdoor.tf).

variable "name_prefix" { type = string }
variable "alb_dns_name" { type = string }
variable "origin_protocol" {
  type    = string
  default = "http-only"
}

data "aws_cloudfront_cache_policy" "disabled" {
  name = "Managed-CachingDisabled"
}

data "aws_cloudfront_origin_request_policy" "all_viewer" {
  name = "Managed-AllViewerExceptHostHeader"
}

resource "aws_cloudfront_cache_policy" "restaurants" {
  name        = "${var.name_prefix}-restaurants-30s"
  default_ttl = 30
  max_ttl     = 30
  min_ttl     = 0

  parameters_in_cache_key_and_forwarded_to_origin {
    enable_accept_encoding_gzip   = true
    enable_accept_encoding_brotli = true
    cookies_config {
      cookie_behavior = "none"
    }
    headers_config {
      header_behavior = "none"
    }
    query_strings_config {
      query_string_behavior = "all"
    }
  }
}

resource "aws_cloudfront_distribution" "this" {
  enabled         = true
  comment         = "${var.name_prefix} edge"
  price_class     = "PriceClass_200" # includes India edge locations
  is_ipv6_enabled = true

  origin {
    domain_name = var.alb_dns_name
    origin_id   = "alb"
    custom_origin_config {
      http_port              = 80
      https_port             = 443
      origin_protocol_policy = var.origin_protocol
      origin_ssl_protocols   = ["TLSv1.2"]
    }
  }

  default_cache_behavior {
    target_origin_id         = "alb"
    viewer_protocol_policy   = "redirect-to-https"
    allowed_methods          = ["GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE"]
    cached_methods           = ["GET", "HEAD"]
    cache_policy_id          = data.aws_cloudfront_cache_policy.disabled.id
    origin_request_policy_id = data.aws_cloudfront_origin_request_policy.all_viewer.id
  }

  ordered_cache_behavior {
    path_pattern             = "/api/v1/restaurants*"
    target_origin_id         = "alb"
    viewer_protocol_policy   = "redirect-to-https"
    allowed_methods          = ["GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE"]
    cached_methods           = ["GET", "HEAD"]
    cache_policy_id          = aws_cloudfront_cache_policy.restaurants.id
    origin_request_policy_id = data.aws_cloudfront_origin_request_policy.all_viewer.id
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    cloudfront_default_certificate = true
  }
}

output "domain_name" {
  value = aws_cloudfront_distribution.this.domain_name
}
