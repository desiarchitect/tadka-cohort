# AWS REFERENCE environment: 4 services + gateway on ECS Fargate (ADR-039, ADR-064).
# `terraform plan` only. It is never applied in the cohort; the live class deploy is Azure (deploy/azure).
# Kept so the AWS picture is honest and complete, and so students can map every Azure box to an AWS box
# (deploy/README.md has the mapping table).
#
#   CloudFront -> ALB -> path rules: /api/v1/orders|payments|deliveries|restaurants -> that service
#                        everything else (/api/v1/auth, /users, /health, /demo)        -> gateway (YARP)
#   Services find each other and Kafka through Cloud Map (<name>.tadka.local).
#   Both routing styles are shown on purpose: ALB rules = AWS-native L7 routing; the gateway = what we run
#   locally and on Azure (where it is the ONLY public app).

terraform {
  required_version = ">= 1.6"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "aws" {
  region = var.aws_region
}

locals {
  name_prefix = "tadka-demo"
  image       = { for k, url in module.registry.repository_urls : k => "${url}:${var.image_tag}" }

  kafka_bootstrap = var.enable_msk ? module.msk[0].bootstrap_brokers : "kafka.tadka.local:9092"
  redis           = "${module.cache.redis_endpoint}:6379,abortConnect=false"

  common_env = {
    ASPNETCORE_ENVIRONMENT  = "Production"
    Kafka__BootstrapServers = local.kafka_bootstrap
  }

  jwt_secret = { Jwt__SigningKey = module.platform.jwt_param_arn }

  # Shared service-module inputs.
  svc = {
    name_prefix        = local.name_prefix
    vpc_id             = module.network.vpc_id
    private_subnets    = module.network.private_subnets
    cluster_id         = module.platform.cluster_id
    cluster_name       = module.platform.cluster_name
    execution_role_arn = module.platform.execution_role_arn
    log_group          = module.platform.log_group
    namespace_id       = module.platform.namespace_id
    security_group_id  = module.platform.internal_sg_id
    listener_arn       = module.edge.listener_arn
  }
}

module "network" {
  source      = "../../modules/network"
  name_prefix = local.name_prefix
}

module "edge" {
  source          = "../../modules/edge"
  name_prefix     = local.name_prefix
  vpc_id          = module.network.vpc_id
  public_subnets  = module.network.public_subnets
  certificate_arn = var.certificate_arn
}

module "cdn" {
  source          = "../../modules/cdn"
  name_prefix     = local.name_prefix
  alb_dns_name    = module.edge.alb_dns_name
  origin_protocol = module.edge.origin_protocol
}

module "platform" {
  source      = "../../modules/platform"
  name_prefix = local.name_prefix
  vpc_id      = module.network.vpc_id
  vpc_cidr    = module.network.vpc_cidr
}

module "registry" {
  source      = "../../modules/registry"
  name_prefix = local.name_prefix
}

module "cache" {
  source            = "../../modules/cache"
  name_prefix       = local.name_prefix
  vpc_id            = module.network.vpc_id
  private_subnets   = module.network.private_subnets
  security_group_id = module.platform.internal_sg_id
}

# Managed Kafka, OFF by default (cost). See modules/msk for the reasoning.
module "msk" {
  count             = var.enable_msk ? 1 : 0
  source            = "../../modules/msk"
  name_prefix       = local.name_prefix
  private_subnets   = module.network.private_subnets
  security_group_id = module.platform.internal_sg_id
}

# Default: one self-hosted KRaft broker (same image and settings as docker-compose). No ALB, no autoscaling,
# ephemeral storage: the Outbox tables can replay anything unsent.
module "kafka" {
  count              = var.enable_msk ? 0 : 1
  source             = "../../modules/service"
  name               = "kafka"
  image              = "apache/kafka:3.8.0"
  container_port     = 9092
  cpu                = 1024
  memory             = 2048
  listener_arn       = ""
  min_count          = 1
  max_count          = 1
  name_prefix        = local.svc.name_prefix
  vpc_id             = local.svc.vpc_id
  private_subnets    = local.svc.private_subnets
  cluster_id         = local.svc.cluster_id
  cluster_name       = local.svc.cluster_name
  execution_role_arn = local.svc.execution_role_arn
  log_group          = local.svc.log_group
  namespace_id       = local.svc.namespace_id
  security_group_id  = local.svc.security_group_id
  environment = {
    KAFKA_NODE_ID                                  = "1"
    KAFKA_PROCESS_ROLES                            = "broker,controller"
    KAFKA_CONTROLLER_QUORUM_VOTERS                 = "1@localhost:9093"
    KAFKA_LISTENERS                                = "CONTROLLER://0.0.0.0:9093,PLAINTEXT://0.0.0.0:9092"
    KAFKA_ADVERTISED_LISTENERS                     = "PLAINTEXT://kafka.tadka.local:9092"
    KAFKA_LISTENER_SECURITY_PROTOCOL_MAP           = "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT"
    KAFKA_CONTROLLER_LISTENER_NAMES                = "CONTROLLER"
    KAFKA_INTER_BROKER_LISTENER_NAME               = "PLAINTEXT"
    KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR         = "1"
    KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR = "1"
    KAFKA_TRANSACTION_STATE_LOG_MIN_ISR            = "1"
    KAFKA_AUTO_CREATE_TOPICS_ENABLE                = "true"
  }
}

# ── The 4 services ───────────────────────────────────────────────────────────────────────────────
module "monolith" {
  source             = "../../modules/service"
  name               = "ordering"
  image              = local.image["api"]
  route_prefix       = "/api/v1/orders"
  min_count          = 1
  max_count          = var.max_count
  create_db          = true
  db_name            = "tadka"
  db_connection_name = "TadkaDb"
  name_prefix        = local.svc.name_prefix
  vpc_id             = local.svc.vpc_id
  private_subnets    = local.svc.private_subnets
  cluster_id         = local.svc.cluster_id
  cluster_name       = local.svc.cluster_name
  execution_role_arn = local.svc.execution_role_arn
  log_group          = local.svc.log_group
  namespace_id       = local.svc.namespace_id
  security_group_id  = local.svc.security_group_id
  listener_arn       = local.svc.listener_arn
  health_check_path  = "/health"
  environment = merge(local.common_env, {
    ConnectionStrings__Redis      = local.redis
    RateLimit__PerMinute          = "100000" # per-IP limit lives at the edge; behind the ALB every IP is a proxy
    Services__Restaurant__BaseUrl = "http://restaurant.tadka.local:8080"
  })
  secrets = merge(local.jwt_secret, { Demo__EncryptionKey = module.platform.pii_key_param_arn })
}

module "payment" {
  source             = "../../modules/service"
  name               = "payment"
  image              = local.image["payment"]
  route_prefix       = "/api/v1/payments"
  min_count          = 1
  max_count          = 2
  create_db          = true
  db_name            = "tadka_payment"
  db_connection_name = "PaymentDb"
  name_prefix        = local.svc.name_prefix
  vpc_id             = local.svc.vpc_id
  private_subnets    = local.svc.private_subnets
  cluster_id         = local.svc.cluster_id
  cluster_name       = local.svc.cluster_name
  execution_role_arn = local.svc.execution_role_arn
  log_group          = local.svc.log_group
  namespace_id       = local.svc.namespace_id
  security_group_id  = local.svc.security_group_id
  listener_arn       = local.svc.listener_arn
  environment        = local.common_env
  secrets            = local.jwt_secret
}

module "delivery" {
  source             = "../../modules/service"
  name               = "delivery"
  image              = local.image["delivery"]
  route_prefix       = "/api/v1/deliveries"
  min_count          = 1
  max_count          = 2
  create_db          = true
  db_name            = "tadka_delivery"
  db_connection_name = "DeliveryDb"
  name_prefix        = local.svc.name_prefix
  vpc_id             = local.svc.vpc_id
  private_subnets    = local.svc.private_subnets
  cluster_id         = local.svc.cluster_id
  cluster_name       = local.svc.cluster_name
  execution_role_arn = local.svc.execution_role_arn
  log_group          = local.svc.log_group
  namespace_id       = local.svc.namespace_id
  security_group_id  = local.svc.security_group_id
  listener_arn       = local.svc.listener_arn
  environment        = merge(local.common_env, { ConnectionStrings__Redis = local.redis })
  secrets            = local.jwt_secret
}

module "restaurant" {
  source             = "../../modules/service"
  name               = "restaurant"
  image              = local.image["restaurant"]
  route_prefix       = "/api/v1/restaurants"
  min_count          = 1
  max_count          = 2
  create_db          = true
  db_name            = "tadka_restaurant"
  db_connection_name = "RestaurantDb"
  name_prefix        = local.svc.name_prefix
  vpc_id             = local.svc.vpc_id
  private_subnets    = local.svc.private_subnets
  cluster_id         = local.svc.cluster_id
  cluster_name       = local.svc.cluster_name
  execution_role_arn = local.svc.execution_role_arn
  log_group          = local.svc.log_group
  namespace_id       = local.svc.namespace_id
  security_group_id  = local.svc.security_group_id
  listener_arn       = local.svc.listener_arn
  environment        = merge(local.common_env, { ConnectionStrings__Redis = local.redis })
  secrets            = local.jwt_secret
}

# ── The gateway: catch-all ALB rule (lowest precedence) ─────────────────────────────────────────
module "gateway" {
  source             = "../../modules/service"
  name               = "gateway"
  image              = local.image["gateway"]
  route_prefix       = "/"
  rule_priority      = 1000 # evaluated AFTER the four service prefixes
  min_count          = 1
  max_count          = var.max_count
  name_prefix        = local.svc.name_prefix
  vpc_id             = local.svc.vpc_id
  private_subnets    = local.svc.private_subnets
  cluster_id         = local.svc.cluster_id
  cluster_name       = local.svc.cluster_name
  execution_role_arn = local.svc.execution_role_arn
  log_group          = local.svc.log_group
  namespace_id       = local.svc.namespace_id
  security_group_id  = local.svc.security_group_id
  listener_arn       = local.svc.listener_arn
  environment = {
    ASPNETCORE_ENVIRONMENT                                             = "Production"
    Gateway__RateLimitPerMinute                                        = "100000"
    ReverseProxy__Clusters__monolith__Destinations__d1__Address        = "http://ordering.tadka.local:8080/"
    ReverseProxy__Clusters__payment__Destinations__d1__Address         = "http://payment.tadka.local:8080/"
    ReverseProxy__Clusters__delivery__Destinations__d1__Address        = "http://delivery.tadka.local:8080/"
    ReverseProxy__Clusters__restaurant__Destinations__stable__Address  = "http://restaurant.tadka.local:8080/"
    ReverseProxy__Clusters__restaurant__Destinations__canary__Address  = "http://restaurant.tadka.local:8080/"
  }
}
