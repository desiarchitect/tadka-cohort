# ElastiCache Redis — menu cache + geo (ADR-018, ADR-034).

variable "name_prefix" { type = string }
variable "vpc_id" { type = string }
variable "private_subnets" { type = list(string) }

resource "aws_elasticache_subnet_group" "this" {
  name       = "${var.name_prefix}-redis"
  subnet_ids = var.private_subnets
}

resource "aws_elasticache_cluster" "this" {
  cluster_id           = "${var.name_prefix}-redis"
  engine               = "redis"
  node_type            = "cache.t3.micro"
  num_cache_nodes      = 1
  parameter_group_name = "default.redis7"
  subnet_group_name    = aws_elasticache_subnet_group.this.name
}

output "redis_endpoint" {
  value = aws_elasticache_cluster.this.cache_nodes[0].address
}