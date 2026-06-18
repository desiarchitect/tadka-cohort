variable "aws_region" {
  type    = string
  default = "ap-south-1"
}

variable "ecr_repo" {
  type        = string
  description = "ECR registry URL prefix"
  default     = "123456789012.dkr.ecr.ap-south-1.amazonaws.com/tadka"
}

variable "image_tag" {
  type    = string
  default = "latest"
}

variable "certificate_arn" {
  type        = string
  description = "ACM cert for ALB HTTPS — required before apply"
  default     = ""
}