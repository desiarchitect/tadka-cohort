# D1-9: Completed Requirements — Answer Key

> **Instructor only.** This is the reference for reviewing student submissions. Students should discover these requirements from the product brief.

## Functional Requirements

### User Management
- **FR-001:** Customers can register using email + password or phone + OTP
- **FR-002:** Users can log in and receive a JWT access token + refresh token
- **FR-003:** Customers can manage multiple delivery addresses (add, edit, delete, set default)
- **FR-004:** Customers can view and update their profile (name, phone, email, preferences)
- **FR-005:** System supports 4 roles: Customer, Restaurant Partner, Delivery Partner, Admin
- **FR-006:** Delivery partners can toggle their availability status (online/offline)

### Restaurant & Menu
- **FR-010:** Admin can onboard a restaurant partner (verify documents, approve listing)
- **FR-011:** Restaurant partner can add, update, and remove menu items (name, description, price, image, category)
- **FR-012:** Restaurant partner can mark items as "available" or "out of stock" in real time
- **FR-013:** Restaurant partner can set operating hours and temporarily close the restaurant
- **FR-014:** Customers can browse restaurants filtered by cuisine, rating, delivery time, and price range
- **FR-015:** Restaurant partner can view a dashboard with daily/weekly order count, revenue, and avg prep time

### Order Management
- **FR-020:** Customer can add items from one restaurant to a cart (no multi-restaurant carts in MVP)
- **FR-021:** Customer can modify cart items (change quantity, remove items) before placing an order
- **FR-022:** Customer can place an order, which triggers payment processing and restaurant notification
- **FR-023:** Order follows a state machine: `placed → confirmed → preparing → ready_for_pickup → picked_up → on_the_way → delivered` (also: `cancelled`, `failed`)
- **FR-024:** Restaurant partner can accept or reject an incoming order (with rejection reason)
- **FR-025:** Customer can cancel an order before the restaurant confirms it (full refund)
- **FR-026:** Customer can view order history with details (items, total, status, timestamps)
- **FR-027:** Customer can reorder from order history (add same items to cart)
- **FR-028:** System assigns a delivery partner automatically after restaurant confirms the order

### Delivery Management
- **FR-030:** System assigns delivery to the nearest available delivery partner based on proximity to the restaurant
- **FR-031:** Delivery partner sees order details: restaurant address, customer address, items summary, special instructions
- **FR-032:** Delivery partner updates order status at each stage: picked up, on the way, delivered
- **FR-033:** Customer can see real-time delivery tracking (delivery partner location on a map)
- **FR-034:** Delivery partner can view their earnings for the day/week
- **FR-035:** System handles reassignment if a delivery partner doesn't accept within 60 seconds

### Payment
- **FR-040:** Customer can pay using UPI, debit/credit card, or cash on delivery (COD)
- **FR-041:** System generates an invoice/receipt for every completed order
- **FR-042:** Customer receives a refund for cancelled orders (auto-refund to original payment method)
- **FR-043:** Restaurant partner settlements are batched and processed weekly
- **FR-044:** Delivery partner payouts are batched and settled weekly

### Search & Discovery
- **FR-050:** Customers can search restaurants and menu items by keyword
- **FR-051:** System sorts nearby restaurants by a combination of distance, rating, and estimated delivery time
- **FR-052:** System shows restaurant availability status, estimated delivery time, and distance in search results

### Ratings & Reviews
- **FR-060:** Customer can rate a delivered order (1-5 stars for food, 1-5 stars for delivery)
- **FR-061:** Customer can write a text review for the restaurant
- **FR-062:** Restaurant rating is the running average of all food ratings

### Admin / Operations
- **FR-070:** Admin can view real-time dashboards: order volume, avg delivery time, order completion rate
- **FR-071:** Admin can handle customer complaints and issue refunds
- **FR-072:** Admin can suspend a restaurant or delivery partner account
- **FR-073:** Admin can view and filter all orders by status, date range, restaurant, customer

---

## Non-Functional Requirements

### Performance
- **NFR-P01:** Order placement P99 latency: **500ms** (from button click to order confirmed in system)
- **NFR-P02:** Menu listing P99 latency: **200ms** (restaurant menu page load)
- **NFR-P03:** Restaurant search P99 latency: **300ms**
- **NFR-P04:** Peak throughput: **3.5 orders/second** (1 lakh daily orders, 3x peak ratio)
- **NFR-P05:** Real-time tracking update frequency: every **5 seconds**

### Availability
- **NFR-A01:** Overall platform availability: **99.9%** (= 8 hours 46 minutes downtime/year)
- **NFR-A02:** Payment processing availability: **99.99%** (= 52 minutes downtime/year). Payments are more critical because failed payments = lost revenue and broken trust.
- **NFR-A03:** Acceptable RTO (Recovery Time Objective): **15 minutes** for a full outage
- **NFR-A04:** RPO (Recovery Point Objective): **1 minute** (max data loss in a crash)

### Scalability
- **NFR-S01:** Target DAU Year 1: **1 lakh** (100,000 daily active users)
- **NFR-S02:** Target daily orders Year 1: **1 lakh** (100,000 orders/day)
- **NFR-S03:** Peak-to-average ratio: **3x** (dinner rush 7-10 PM)
- **NFR-S04:** Geographic expansion: Bangalore only in Year 1. Hyderabad and Pune in Year 2.
- **NFR-S05:** Restaurant partners Year 1: **5,000** (starting with 100, 20% MoM growth)

### Consistency
- **NFR-C01:** Order state: **Strong consistency.** An order is either placed or not. No phantom orders. No duplicate orders. This is non-negotiable.
- **NFR-C02:** Payment state: **Strong consistency.** Payment is either captured or not. Double-charging a customer is a business-ending event.
- **NFR-C03:** Menu/pricing: **Eventual consistency** (acceptable: up to 30 seconds stale). If a restaurant updates a price, it's ok if the old price shows for a few seconds. But the order must use the price at the time of placement.
- **NFR-C04:** Analytics/dashboards: **Eventual consistency** (acceptable: up to 5 minutes stale). Admin dashboards don't need real-time data.
- **NFR-C05:** Delivery partner location: **Eventual consistency** (acceptable: up to 10 seconds stale). GPS updates are inherently laggy.

### Security
- **NFR-SEC01:** Authentication: JWT access tokens (15 min expiry) + refresh tokens (7 day expiry). Phone OTP for customer registration.
- **NFR-SEC02:** Data encryption: TLS 1.3 in transit, AES-256 at rest for PII and payment data
- **NFR-SEC03:** PII handling: Customer phone numbers and addresses are masked in logs. Only the assigned delivery partner sees the full address.
- **NFR-SEC04:** Payment compliance: PCI DSS Level 4 (SAQ A, since we use a payment gateway like Razorpay, not direct card processing). We never store full card numbers.
- **NFR-SEC05:** API authentication: All endpoints require a valid JWT except `/health`, `/health/ready`, and public restaurant listings.

---

## Questions a Good Architect Would Ask the PM

> These are the questions students should identify as missing from the product brief. Review student submissions for these.

1. **"High traffic" means what exactly?** What's the target peak QPS? What's the dinner rush profile?
2. **"Payments should be fast" means what?** What's the acceptable P99 latency for payment processing?
3. **"Platform should be reliable" means what?** What's the availability target? 99.9%? 99.99%? Each 9 costs significantly more.
4. **What happens when the payment gateway is down?** Do we retry? Queue orders? Show an error? Accept COD as fallback?
5. **What happens when no delivery partner is available?** Do we cancel the order? Queue it? Notify the customer with an ETA?
6. **What's the budget for infrastructure?** AWS costs scale with traffic. A 99.99% availability target costs 10x more than 99.9%.
7. **Is there a maximum delivery radius?** 5km? 10km? City-wide?
8. **How do we handle order disputes?** Customer says food was wrong. Restaurant says it was correct. Who decides?
9. **What data do we need to keep and for how long?** Order history: 1 year? 5 years? Forever? Compliance requirements?
10. **Multi-restaurant carts?** Can a customer order from 2 restaurants in one order? (Product brief doesn't say. Answer: No, not in MVP.)

---

## Scoring Guide for Student Submissions

| Criteria | Score |
|----------|-------|
| Identified 15+ FRs across at least 4 domains | Good |
| Identified 20+ FRs across all 5 domains | Excellent |
| Included order state machine or mentioned state transitions | Bonus |
| NFRs have specific numbers (not just "fast" or "reliable") | Good |
| NFRs distinguish between domains (payment vs menu consistency) | Excellent |
| Listed 3+ questions for the PM | Good |
| Questions address the deliberately vague areas (traffic, latency, reliability) | Excellent |

### Common Mistakes Students Make
1. **Skipping NFRs entirely.** They list features but not quality attributes. Point out: "You told me what to build. You didn't tell me how well it needs to work."
2. **Saying 99.999% availability without understanding the cost.** Ask: "Do you know how much it costs to go from 99.9% to 99.99%? About 10x. And from 99.99% to 99.999%? Another 10x. For a startup with 3 months of runway?"
3. **Not distinguishing consistency models.** Everything is "strongly consistent." Ask: "Does a customer need to see a menu price change within 1 millisecond? Or is 30 seconds fine? That answer changes your architecture."
4. **Forgetting failure scenarios.** No mention of what happens when things break. Ask: "Your payment gateway returns a timeout. What does your system do?"
