# Tadka - poison-message injector for the menu-updated schema-evolution demo (ADR-050, sibling to
# scripts/inject-poison.ps1 which does the same thing for Payment's order-placed).
#
# Publishes a single raw message directly onto the menu-updated topic, bypassing the real Restaurant
# producer, to simulate a Restaurant-service deploy that changed the event shape without coordinating
# with Ordering's MenuUpdatedConsumer (the two sides deliberately don't share a contract type, ADR-050).
#
#   pwsh scripts/inject-poison-menu.ps1 -Mode SafeExtraField    # one EXTRA unknown field -> applies fine
#   pwsh scripts/inject-poison-menu.ps1 -Mode BreakingRename    # PriceAmount renamed to Price -> NO throw, price silently becomes 0
#
# Watch the effect with:
#   docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\",\"PriceAmount\" FROM ordering.menu_replica WHERE \"MenuItemId\"='b1b2c3d4-0001-4000-8000-000000000001';"
#   docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --describe --group tadka-monolith-menu

param(
    [ValidateSet("SafeExtraField", "BreakingRename")]
    [string]$Mode = "BreakingRename"
)

# Real seeded restaurant + menu item (Day-12 seed, MenuReplica.cs / Restaurant.Api's own seed) so the
# upsert lands on a row you can query before/after and compare against the known-good price (Rs 299,
# or Rs 349 if break-kit-day-12.md's Beat 2 has already run on this database).
$restaurantId = "a1b2c3d4-0001-4000-8000-000000000001"   # Meghana Foods
$menuItemId   = "b1b2c3d4-0001-4000-8000-000000000001"   # Chicken Biryani
$messageId    = [guid]::NewGuid().ToString()

# RestaurantSnapshotMessage(MessageId, RestaurantId, Name, IsActive, Address, Menu[]) — the real shape
# both Tadka.Restaurant.Api (producer) and Tadka.Api's MenuUpdatedConsumer (consumer) each define their
# own copy of (ADR-050's "no shared library, by design"). MenuItemSnapshot(MenuItemId, Name, PriceAmount,
# PriceCurrency, IsAvailable, Category, IsVeg).
$address = '"Address":{"Line1":"12, Church Street","Line2":"","City":"Bangalore","Pincode":"560001","Latitude":12.9752,"Longitude":77.6050}'

$payload = switch ($Mode) {
    "BreakingRename" {
        # A bad deploy renamed PriceAmount -> Price without a compatibility shim (ADR-050 violation) —
        # the mirror of the Payment demo's Currency -> CurrencyCode rename, but sharper: decimal's CLR
        # default is 0, not null, so the missing constructor parameter silently prices the dish at
        # Rs 0.00 instead of leaving a null a DB column-default could paper over.
        '{"MessageId":"' + $messageId + '","RestaurantId":"' + $restaurantId + '","Name":"Meghana Foods","IsActive":true,' + $address + `
          ',"Menu":[{"MenuItemId":"' + $menuItemId + '","Name":"Chicken Biryani","Price":299.00,"PriceCurrency":"INR","IsAvailable":true,"Category":"Biryani","IsVeg":false}]}'
    }
    "SafeExtraField" {
        # Additive-only evolution: a genuinely new field (SpiceLevel) the consumer doesn't know about yet.
        '{"MessageId":"' + $messageId + '","RestaurantId":"' + $restaurantId + '","Name":"Meghana Foods","IsActive":true,' + $address + `
          ',"Menu":[{"MenuItemId":"' + $menuItemId + '","Name":"Chicken Biryani","PriceAmount":299.00,"PriceCurrency":"INR","IsAvailable":true,"Category":"Biryani","IsVeg":false,"SpiceLevel":"Medium"}]}'
    }
}

Write-Output "Injecting [$Mode] onto menu-updated (key=$restaurantId):"
Write-Output $payload

$key = "$restaurantId="
$line = "$key$payload"
$line | docker exec -i tadka-kafka /opt/kafka/bin/kafka-console-producer.sh `
    --bootstrap-server localhost:9092 --producer.config /etc/kafka/docker/client.properties --topic menu-updated `
    --property "parse.key=true" --property "key.separator==" 2>$null

Write-Output ""
switch ($Mode) {
    "SafeExtraField" {
        Write-Output "Expected: the replica still upserts Chicken Biryani at Rs 299.00 - System.Text.Json ignores the unknown SpiceLevel field."
        Write-Output "This is what 'additive-only' schema evolution buys you: Restaurant.Api can ship SpiceLevel today; Ordering picks it up whenever it adds the field, zero coordination required."
    }
    "BreakingRename" {
        Write-Output "Expected: NO exception, NO retry, NO log line at all - it just processes. System.Text.Json binds"
        Write-Output "the missing 'PriceAmount' constructor parameter to decimal's default, 0m (not an error, and unlike"
        Write-Output "Payment's string/null case, there is no column default to paper over it - MenuItemReplica.PriceAmount"
        Write-Output "has no HasDefaultValue). The upsert 'succeeds': ordering.menu_replica now prices Chicken Biryani at"
        Write-Output "Rs 0.00, and the NEXT order placed for it is priced from that replica - a free biryani, silently,"
        Write-Output "same lesson as ADR-050's Currency rename but with a sharper consequence because there is no DB"
        Write-Output "default in this table to soften the blow."
    }
}
