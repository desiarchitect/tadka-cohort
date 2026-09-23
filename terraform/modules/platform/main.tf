# Shared platform pieces every ECS service uses: cluster, task execution role, logs, service discovery,
# one internal security group, and the shared secrets (JWT key, PII key) in SSM Parameter Store.

variable "name_prefix" { type = string }
variable "vpc_id" { type = string }
variable "vpc_cidr" { type = string }

resource "aws_ecs_cluster" "this" {
  name = "${var.name_prefix}-cluster"
  setting {
    name  = "containerInsights"
    value = "enabled"
  }
}

resource "aws_cloudwatch_log_group" "this" {
  name              = "/ecs/${var.name_prefix}"
  retention_in_days = 7
}

# Cloud Map: services find each other as <name>.tadka.local (the gateway's YARP destinations, Kafka).
resource "aws_service_discovery_private_dns_namespace" "this" {
  name = "tadka.local"
  vpc  = var.vpc_id
}

# One SG for everything inside the VPC (services, RDS, ElastiCache, Kafka). Reference-grade; production
# would split it per tier (ALB -> service -> data) with SG-to-SG rules.
resource "aws_security_group" "internal" {
  name   = "${var.name_prefix}-internal-sg"
  vpc_id = var.vpc_id

  ingress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = [var.vpc_cidr]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

# Task EXECUTION role: pull images, write logs, read the SSM secrets injected as env vars.
resource "aws_iam_role" "execution" {
  name = "${var.name_prefix}-ecs-execution"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "ecs-tasks.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_caller_identity" "current" {}
data "aws_region" "current" {}

resource "aws_iam_role_policy" "execution_ssm" {
  name = "read-tadka-secrets"
  role = aws_iam_role.execution.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["ssm:GetParameters"]
      Resource = "arn:aws:ssm:${data.aws_region.current.name}:${data.aws_caller_identity.current.account_id}:parameter/${var.name_prefix}/*"
    }]
  })
}

# Shared secrets. random_password, never a literal in HCL (the old "CHANGE_ME" is gone).
resource "random_password" "jwt" {
  length  = 64
  special = false
}

resource "random_bytes" "pii_key" {
  length = 32
}

resource "aws_ssm_parameter" "jwt" {
  name  = "/${var.name_prefix}/jwt-signing-key"
  type  = "SecureString"
  value = random_password.jwt.result
}

resource "aws_ssm_parameter" "pii_key" {
  name  = "/${var.name_prefix}/pii-encryption-key"
  type  = "SecureString"
  value = random_bytes.pii_key.base64
}

output "cluster_id" {
  value = aws_ecs_cluster.this.id
}

output "cluster_name" {
  value = aws_ecs_cluster.this.name
}

output "execution_role_arn" {
  value = aws_iam_role.execution.arn
}

output "log_group" {
  value = aws_cloudwatch_log_group.this.name
}

output "namespace_id" {
  value = aws_service_discovery_private_dns_namespace.this.id
}

output "internal_sg_id" {
  value = aws_security_group.internal.id
}

output "jwt_param_arn" {
  value = aws_ssm_parameter.jwt.arn
}

output "pii_key_param_arn" {
  value = aws_ssm_parameter.pii_key.arn
}
