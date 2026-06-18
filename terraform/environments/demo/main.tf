# Instructor demo environment — 4 services + gateway shape (ADR-039).
# terraform plan only in class; apply costs real AWS money.

terraform {
  required_version = ">= 1.5"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }
}

provider "aws" {
  region = var.aws_region
}

locals {
  name_prefix = "tadka-demo"
}

module "network" {
  source       = "../../modules/network"
  name_prefix  = local.name_prefix
}

module "edge" {
  source         = "../../modules/edge"
  name_prefix    = local.name_prefix
  vpc_id         = module.network.vpc_id
  public_subnets = module.network.public_subnets
  certificate_arn = var.certificate_arn
}

module "cache" {
  source          = "../../modules/cache"
  name_prefix     = local.name_prefix
  vpc_id          = module.network.vpc_id
  private_subnets = module.network.private_subnets
}

module "msk" {
  source          = "../../modules/msk"
  name_prefix     = local.name_prefix
  private_subnets = module.network.private_subnets
}

module "monolith" {
  source             = "../../modules/service"
  name               = "ordering"
  name_prefix        = local.name_prefix
  vpc_id             = module.network.vpc_id
  private_subnets    = module.network.private_subnets
  alb_listener_arn   = module.edge.https_listener_arn
  route_prefix       = "/api/v1/orders"
  image              = "${var.ecr_repo}/ordering:${var.image_tag}"
}

module "payment" {
  source             = "../../modules/service"
  name               = "payment"
  name_prefix        = local.name_prefix
  vpc_id             = module.network.vpc_id
  private_subnets    = module.network.private_subnets
  alb_listener_arn   = module.edge.https_listener_arn
  route_prefix       = "/api/v1/payments"
  image              = "${var.ecr_repo}/payment:${var.image_tag}"
}

module "delivery" {
  source             = "../../modules/service"
  name               = "delivery"
  name_prefix        = local.name_prefix
  vpc_id             = module.network.vpc_id
  private_subnets    = module.network.private_subnets
  alb_listener_arn   = module.edge.https_listener_arn
  route_prefix       = "/api/v1/deliveries"
  image              = "${var.ecr_repo}/delivery:${var.image_tag}"
}

module "restaurant" {
  source             = "../../modules/service"
  name               = "restaurant"
  name_prefix        = local.name_prefix
  vpc_id             = module.network.vpc_id
  private_subnets    = module.network.private_subnets
  alb_listener_arn   = module.edge.https_listener_arn
  route_prefix       = "/api/v1/restaurants"
  image              = "${var.ecr_repo}/restaurant:${var.image_tag}"
}