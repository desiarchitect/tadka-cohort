# ALB + listener: mirrors YARP path routing locally (ADR-035, ADR-039).
# certificate_arn is OPTIONAL: empty = an HTTP:80 listener that only CloudFront talks to (CloudFront
# terminates TLS with its default certificate). Set it to an ACM cert ARN for an HTTPS:443 listener.

variable "name_prefix" { type = string }
variable "vpc_id" { type = string }
variable "public_subnets" { type = list(string) }
variable "certificate_arn" {
  type    = string
  default = ""
}

locals {
  https = var.certificate_arn != ""
}

resource "aws_security_group" "alb" {
  name   = "${var.name_prefix}-alb-sg"
  vpc_id = var.vpc_id

  ingress {
    from_port   = local.https ? 443 : 80
    to_port     = local.https ? 443 : 80
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"] # production: restrict to the CloudFront origin-facing prefix list
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_lb" "this" {
  name               = "${var.name_prefix}-alb"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb.id]
  subnets            = var.public_subnets
}

resource "aws_lb_listener" "https" {
  count             = local.https ? 1 : 0
  load_balancer_arn = aws_lb.this.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = var.certificate_arn

  default_action {
    type = "fixed-response"
    fixed_response {
      content_type = "text/plain"
      message_body = "no route"
      status_code  = "404"
    }
  }
}

resource "aws_lb_listener" "http" {
  count             = local.https ? 0 : 1
  load_balancer_arn = aws_lb.this.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type = "fixed-response"
    fixed_response {
      content_type = "text/plain"
      message_body = "no route"
      status_code  = "404"
    }
  }
}

output "alb_arn" {
  value = aws_lb.this.arn
}

output "alb_security_group_id" {
  value = aws_security_group.alb.id
}

output "listener_arn" {
  value = local.https ? aws_lb_listener.https[0].arn : aws_lb_listener.http[0].arn
}

output "alb_dns_name" {
  value = aws_lb.this.dns_name
}

output "origin_protocol" {
  value = local.https ? "https-only" : "http-only"
}
