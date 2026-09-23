# One ECS Fargate service: task definition + service + (optional) ALB target group and path rule +
# (optional) its OWN RDS Postgres (database-per-service, ADR-026) + Cloud Map name + CPU autoscaling.
# Used for the 4 services, the gateway, and the self-hosted Kafka broker.

variable "name" { type = string }
variable "name_prefix" { type = string }
variable "vpc_id" { type = string }
variable "private_subnets" { type = list(string) }
variable "image" { type = string }

variable "cluster_id" { type = string }
variable "cluster_name" { type = string }
variable "execution_role_arn" { type = string }
variable "log_group" { type = string }
variable "namespace_id" { type = string }
variable "security_group_id" { type = string }

variable "container_port" {
  type    = number
  default = 8080
}
variable "cpu" {
  type    = number
  default = 512
}
variable "memory" {
  type    = number
  default = 1024
}

# ALB wiring. listener_arn = "" means no load balancer (e.g. Kafka).
variable "listener_arn" {
  type    = string
  default = ""
}
variable "route_prefix" {
  type    = string
  default = "/"
}
variable "rule_priority" {
  type    = number
  default = null
}
variable "health_check_path" {
  type    = string
  default = "/health"
}

# Scaling. min == max means no autoscaling resources are created.
variable "min_count" {
  type    = number
  default = 1
}
variable "max_count" {
  type    = number
  default = 4
}
variable "cpu_target_percent" {
  type    = number
  default = 60
}

# Plain env vars and SSM-backed secrets (env name -> SSM parameter ARN).
variable "environment" {
  type    = map(string)
  default = {}
}
variable "secrets" {
  type    = map(string)
  default = {}
}

# Own database (database-per-service). create_db = false for the gateway and Kafka.
variable "create_db" {
  type    = bool
  default = false
}
variable "db_name" {
  type    = string
  default = ""
}
variable "db_connection_name" {
  description = "The ConnectionStrings:<name> key the service reads (TadkaDb, PaymentDb, ...)."
  type        = string
  default     = ""
}
variable "db_instance_class" {
  type    = string
  default = "db.t4g.micro"
}

locals {
  attach_alb  = var.listener_arn != ""
  autoscale   = var.max_count > var.min_count
  db_env_name = "ConnectionStrings__${var.db_connection_name}"
}

data "aws_region" "current" {}

# ── Database (optional) ─────────────────────────────────────────────────────────────────────────
resource "random_password" "db" {
  count            = var.create_db ? 1 : 0
  length           = 32
  special          = true
  override_special = "-_" # no ; or = : the password goes into an Npgsql connection string
}

resource "aws_db_subnet_group" "this" {
  count      = var.create_db ? 1 : 0
  name       = "${var.name_prefix}-${var.name}-db"
  subnet_ids = var.private_subnets
}

resource "aws_db_instance" "this" {
  count                  = var.create_db ? 1 : 0
  identifier             = "${var.name_prefix}-${var.name}"
  engine                 = "postgres"
  engine_version         = "16"
  instance_class         = var.db_instance_class
  allocated_storage      = 20
  db_name                = var.db_name
  username               = "tadka"
  password               = random_password.db[0].result
  db_subnet_group_name   = aws_db_subnet_group.this[0].name
  vpc_security_group_ids = [var.security_group_id]
  publicly_accessible    = false
  skip_final_snapshot    = true
}

# The full connection string is a SecureString in SSM, injected by ECS as an env var at task start.
resource "aws_ssm_parameter" "db" {
  count = var.create_db ? 1 : 0
  name  = "/${var.name_prefix}/${var.name}/db-connection-string"
  type  = "SecureString"
  value = "Host=${aws_db_instance.this[0].address};Port=5432;Database=${var.db_name};Username=tadka;Password=${random_password.db[0].result};SSL Mode=Require;Maximum Pool Size=20"
}

# ── ALB target group + path rule (optional) ─────────────────────────────────────────────────────
resource "aws_lb_target_group" "this" {
  count       = local.attach_alb ? 1 : 0
  name        = substr("${var.name_prefix}-${var.name}", 0, 32)
  port        = var.container_port
  protocol    = "HTTP"
  vpc_id      = var.vpc_id
  target_type = "ip"
  health_check {
    path    = var.health_check_path
    matcher = "200"
  }
}

resource "aws_lb_listener_rule" "this" {
  count        = local.attach_alb ? 1 : 0
  listener_arn = var.listener_arn
  priority     = coalesce(var.rule_priority, 100 + length(var.route_prefix))

  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.this[0].arn
  }

  condition {
    path_pattern {
      values = ["${trimsuffix(var.route_prefix, "/")}*"]
    }
  }
}

# ── Service discovery: <name>.tadka.local ────────────────────────────────────────────────────────
resource "aws_service_discovery_service" "this" {
  name = var.name

  dns_config {
    namespace_id   = var.namespace_id
    routing_policy = "MULTIVALUE"
    dns_records {
      ttl  = 10
      type = "A"
    }
  }

  health_check_custom_config {
    failure_threshold = 1
  }
}

# ── Task definition + service ────────────────────────────────────────────────────────────────────
resource "aws_ecs_task_definition" "this" {
  family                   = "${var.name_prefix}-${var.name}"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.cpu
  memory                   = var.memory
  execution_role_arn       = var.execution_role_arn

  container_definitions = jsonencode([{
    name         = var.name
    image        = var.image
    essential    = true
    portMappings = [{ containerPort = var.container_port, protocol = "tcp" }]

    environment = [for k, v in var.environment : { name = k, value = v }]

    secrets = concat(
      [for k, arn in var.secrets : { name = k, valueFrom = arn }],
      var.create_db ? [{ name = local.db_env_name, valueFrom = aws_ssm_parameter.db[0].arn }] : []
    )

    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = var.log_group
        awslogs-region        = data.aws_region.current.name
        awslogs-stream-prefix = var.name
      }
    }
  }])
}

resource "aws_ecs_service" "this" {
  name            = var.name
  cluster         = var.cluster_id
  task_definition = aws_ecs_task_definition.this.arn
  desired_count   = var.min_count # min 1 first: startup migrations run alone (ADR-064)
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = var.private_subnets
    security_groups  = [var.security_group_id]
    assign_public_ip = false
  }

  dynamic "load_balancer" {
    for_each = local.attach_alb ? [1] : []
    content {
      target_group_arn = aws_lb_target_group.this[0].arn
      container_name   = var.name
      container_port   = var.container_port
    }
  }

  service_registries {
    registry_arn = aws_service_discovery_service.this.arn
  }

  lifecycle {
    ignore_changes = [desired_count] # autoscaling owns it after create
  }

  depends_on = [aws_lb_listener_rule.this]
}

# ── Autoscaling: target-tracking on average CPU ─────────────────────────────────────────────────
resource "aws_appautoscaling_target" "this" {
  count              = local.autoscale ? 1 : 0
  service_namespace  = "ecs"
  resource_id        = "service/${var.cluster_name}/${aws_ecs_service.this.name}"
  scalable_dimension = "ecs:service:DesiredCount"
  min_capacity       = var.min_count
  max_capacity       = var.max_count
}

resource "aws_appautoscaling_policy" "cpu" {
  count              = local.autoscale ? 1 : 0
  name               = "${var.name_prefix}-${var.name}-cpu"
  policy_type        = "TargetTrackingScaling"
  service_namespace  = aws_appautoscaling_target.this[0].service_namespace
  resource_id        = aws_appautoscaling_target.this[0].resource_id
  scalable_dimension = aws_appautoscaling_target.this[0].scalable_dimension

  target_tracking_scaling_policy_configuration {
    target_value       = var.cpu_target_percent
    scale_in_cooldown  = 120
    scale_out_cooldown = 60
    predefined_metric_specification {
      predefined_metric_type = "ECSServiceAverageCPUUtilization"
    }
  }
}

output "service_name" {
  value = aws_ecs_service.this.name
}

output "internal_address" {
  description = "Cloud Map DNS name other services use."
  value       = "${var.name}.tadka.local"
}

output "target_group_arn" {
  value = local.attach_alb ? aws_lb_target_group.this[0].arn : null
}

output "db_endpoint" {
  value = var.create_db ? aws_db_instance.this[0].address : null
}
