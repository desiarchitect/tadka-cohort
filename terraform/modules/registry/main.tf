# ECR: one repository per image (the same 5 images CI pushes to GHCR for the Azure live deploy).

variable "name_prefix" { type = string }
variable "repositories" {
  type    = list(string)
  default = ["api", "payment", "delivery", "restaurant", "gateway"]
}

resource "aws_ecr_repository" "this" {
  for_each             = toset(var.repositories)
  name                 = "${var.name_prefix}/${each.value}"
  image_tag_mutability = "MUTABLE"
  force_delete         = true # reference stack: destroy should not get stuck on leftover images

  image_scanning_configuration {
    scan_on_push = true
  }
}

output "repository_urls" {
  value = { for k, r in aws_ecr_repository.this : k => r.repository_url }
}
