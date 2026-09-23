variable "aws_region" {
  type    = string
  default = "ap-south-1" # Mumbai
}

variable "image_tag" {
  type    = string
  default = "latest"
}

variable "certificate_arn" {
  type        = string
  description = "OPTIONAL ACM certificate ARN for an HTTPS:443 ALB listener. Empty = HTTP:80 listener behind CloudFront (CloudFront terminates TLS)."
  default     = ""
}

variable "enable_msk" {
  type        = bool
  description = "true = managed MSK (2 brokers, the biggest line on the bill). false (default) = one self-hosted KRaft broker on ECS."
  default     = false
}

variable "max_count" {
  type        = number
  description = "Autoscaling ceiling for the ordering service and the gateway."
  default     = 4
}
