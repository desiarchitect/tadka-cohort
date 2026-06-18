# One ECS Fargate service + target group + path rule + dedicated RDS (ADR-026).

variable "name" { type = string }
variable "name_prefix" { type = string }
variable "vpc_id" { type = string }
variable "private_subnets" { type = list(string) }
variable "alb_listener_arn" { type = string }
variable "route_prefix" { type = string }
variable "container_port" {
  type    = number
  default = 8080
}
variable "desired_count" {
  type    = number
  default = 2
}
variable "image" { type = string }
variable "db_instance_class" {
  type    = string
  default = "db.t3.small"
}

resource "aws_lb_target_group" "this" {
  name        = "${var.name_prefix}-${var.name}"
  port        = var.container_port
  protocol    = "HTTP"
  vpc_id      = var.vpc_id
  target_type = "ip"
  health_check { path = "/health" }
}

resource "aws_lb_listener_rule" "this" {
  listener_arn = var.alb_listener_arn
  priority     = 100 + length(var.route_prefix)
  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.this.arn
  }
  condition {
    path_pattern { values = ["${var.route_prefix}*"] }
  }
}

resource "aws_db_instance" "this" {
  identifier     = "${var.name_prefix}-${var.name}"
  engine         = "postgres"
  engine_version = "16"
  instance_class = var.db_instance_class
  allocated_storage = 20
  username       = "tadka"
  password       = "CHANGE_ME" # use Secrets Manager in a real stack
  db_subnet_group_name = aws_db_subnet_group.this.name
  skip_final_snapshot  = true
}

resource "aws_db_subnet_group" "this" {
  name       = "${var.name_prefix}-${var.name}-db"
  subnet_ids = var.private_subnets
}

# ECS service wiring is abbreviated — wire task definition + service to the TG in a full stack.
output "target_group_arn" {
  value = aws_lb_target_group.this.arn
}

output "db_endpoint" {
  value = aws_db_instance.this.address
}