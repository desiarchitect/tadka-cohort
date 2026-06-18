# ALB + HTTPS listener — mirrors YARP path routing locally (ADR-035, ADR-039).

variable "name_prefix" { type = string }
variable "vpc_id" { type = string }
variable "public_subnets" { type = list(string) }
variable "certificate_arn" {
  type    = string
  default = ""
}

resource "aws_lb" "this" {
  name               = "${var.name_prefix}-alb"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb.id]
  subnets            = var.public_subnets
}

resource "aws_security_group" "alb" {
  name   = "${var.name_prefix}-alb-sg"
  vpc_id = var.vpc_id
  ingress {
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }
  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_lb_listener" "https" {
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

output "alb_arn" {
  value = aws_lb.this.arn
}

output "https_listener_arn" {
  value = aws_lb_listener.https.arn
}

output "alb_dns_name" {
  value = aws_lb.this.dns_name
}