# Live cloud deploy of Tadka on Azure (ADR-064). One resource group per class session:
# `scripts/cloud-up.ps1 -Mode basic|ha` applies this, `scripts/cloud-down.ps1` destroys it.
# Instructor reference, not class material ("results, not HCL").

terraform {
  required_version = ">= 1.6.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.40"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # Local state on purpose: the whole environment lives for one session and is destroyed after.
  # State (and its secrets) stays on the instructor's machine; deploy/azure/.gitignore keeps it out of git.
}

provider "azurerm" {
  # azurerm 4.x needs a subscription. cloud-up.ps1 sets ARM_SUBSCRIPTION_ID from `az account show`.
  subscription_id = var.subscription_id

  features {
    resource_group {
      # Teardown must be one command. Container Apps create helper resources in the RG.
      prevent_deletion_if_contains_resources = false
    }
  }
}
