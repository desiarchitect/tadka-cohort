# Tadka — Product Brief

> This document is what a PM would hand you on day one. Read it like an architect: figure out what's clear, what's vague, and what questions you'd ask before writing a single line of code.

## Company Context

Tadka is a food delivery startup launching in Bangalore. The founding team has experience at Swiggy and Dunzo, and they want to build a platform that does three things well: fast ordering, reliable delivery, and a great restaurant partner experience.

The company has seed funding, a team of 6 engineers, and 3 months of runway to build and launch an MVP.

## User Personas

### Customer (orders food)
- Urban professionals, 22-40 years old, mostly ordering dinner
- Expects to find a restaurant, browse the menu, place an order, pay online, and track delivery in real time
- Gets annoyed if food arrives cold or late. Price-sensitive but will pay ₹30-50 delivery fee for a good experience
- Orders 3-5 times a week on average

### Restaurant Partner (manages listings)
- Medium-sized restaurants in Bangalore. Most already on Swiggy/Zomato but looking for better commission rates
- Needs to manage their menu (items, prices, availability), view incoming orders, accept or reject them, and mark orders as ready for pickup
- Kitchen staff isn't tech-savvy. The interface needs to be dead simple
- Handles 50-200 orders per day per restaurant

### Delivery Partner (picks up and delivers)
- Gig workers using Android phones (mix of mid-range and budget devices)
- Needs to see assigned orders, navigate to restaurant, confirm pickup, navigate to customer, confirm delivery
- Gets paid per delivery. Wants maximum orders with minimum idle time
- Works in shifts, mostly evening (6 PM to 11 PM)

### Admin (monitors platform)
- Internal operations team (2-3 people initially)
- Needs dashboards for order volume, delivery times, customer complaints, restaurant performance
- Handles disputes: wrong items, late delivery, payment issues
- Onboards new restaurants

## Core User Flows

### Ordering Flow
Customer opens the app, sees nearby restaurants sorted by rating and delivery time. They pick a restaurant, browse the menu, add items to cart, review their order, and place it. They pay using UPI, card, or wallet. After placing the order, they see live tracking: order confirmed → restaurant preparing → delivery partner assigned → picked up → on the way → delivered.

### Restaurant Flow
Restaurant partner logs in and sees a dashboard with incoming orders. When a new order comes in, they accept or reject it (with a reason). Once accepted, the kitchen prepares it and the partner marks it "ready for pickup." They can also update menu items, toggle availability, and see daily/weekly revenue summaries.

### Delivery Flow
Delivery partner opens the app and goes "online." The system assigns them an order based on proximity to the restaurant. They see restaurant address, navigate there, pick up the food, then see customer address and navigate there. After delivery, they mark it as complete. Payment for the delivery gets settled weekly.

### Admin Flow
Admin logs into a web dashboard. They can see real-time order volume, average delivery times, and customer ratings. They handle escalated complaints, process refunds, and onboard new restaurants (verify documents, approve listings).

## Scale Targets

- **City 1 (Bangalore):** Launch with 100 restaurant partners. Target 1 lakh daily orders within year 1.
- **City 2-3 (Hyderabad, Pune):** Expand in year 2.
- Growth assumption: 20% month-over-month for the first 6 months, then 10% MoM.

## Success Metrics

- Order completion rate: >95%
- Average delivery time: <35 minutes (from order placed to delivered)
- Payment success rate: >99.5%
- Customer retention (monthly): >60%
- Restaurant partner NPS: >40

## Technical Preferences

The team wants to use .NET because that's what the senior engineers know well. PostgreSQL for the database. Docker for local development. AWS for deployment (ECS Fargate). GitHub for code hosting.

The platform should handle high traffic, especially during dinner rush (7 PM to 10 PM). Payments should be fast. The platform should be reliable. We can't afford to lose orders.

## What's NOT in MVP

- Multi-city support (Bangalore only for launch)
- Subscription plans ("Tadka Pro" with free delivery)
- Restaurant analytics beyond basic revenue
- AI-based recommendations
- Group ordering
- Scheduled orders (order now, deliver later)

## Competitors

Swiggy, Zomato, and to some extent Dunzo (for quick commerce). We're not competing with them head-on. We differentiate on lower commissions for restaurants (15% vs 25-30%) and faster onboarding (2 days vs 2 weeks).

---

> **For the architect:** This brief is deliberately incomplete. A real PM brief always is. Your job is to find the gaps, ask the right questions, and turn this into a technical requirements document with actual numbers.
