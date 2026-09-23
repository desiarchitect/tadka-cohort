# The main cost risk is a forgotten `cloud-down` (ADR-064). A budget on the session resource group emails
# a human at 50% and 100% of var.budget_amount (default 1000, in the billing currency: INR for an
# India-billed account). A budget ALERTS; it never stops anything. cloud-down.ps1 is the real control.

resource "azurerm_consumption_budget_resource_group" "session" {
  name              = "budget-tadka-session"
  resource_group_id = azurerm_resource_group.session.id
  amount            = var.budget_amount
  time_grain        = "Monthly"

  time_period {
    # Budgets must start on the first of a month.
    start_date = formatdate("YYYY-MM-01'T'00:00:00Z", timestamp())
  }

  notification {
    enabled        = true
    threshold      = 50
    operator       = "GreaterThan"
    threshold_type = "Actual"
    contact_emails = var.budget_alert_emails
  }

  notification {
    enabled        = true
    threshold      = 100
    operator       = "GreaterThan"
    threshold_type = "Forecasted"
    contact_emails = var.budget_alert_emails
  }

  lifecycle {
    ignore_changes = [time_period]
  }
}
