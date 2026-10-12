# Challenge details

This file is for the candidate. It expands the README without prescribing implementation.

## Domain

Users, products, customers, orders, and order items are already modeled and seeded.

`LAPTOP-001` starts with **5** units. Use it for the concurrent-order scenario.

`CUST-005` is inactive so you can test inactive-customer validation.

## Main flow

```text
Login → view products → create order
  → validate customer/products/quantity/stock
  → persist order + items
  → update stock
```

You decide how to keep that flow consistent when something fails, and when two requests arrive at the same time.

## APIs to implement

| Method | Path | Notes |
| ------ | ---- | ----- |
| POST | `/api/auth/login` | Issue JWT. Design claims and response. |
| GET | `/api/products` | Search, active filter, pagination. |
| GET | `/api/customers` | Optional helper for the create-order screen. |
| POST | `/api/orders` | Create order. Design the request DTO. |
| GET | `/api/orders` | Search by number; filter customer/status/date; sort; paginate in SQL. |
| GET | `/api/orders/{id}` | Historical unit prices on items. |
| GET | `/api/reports/top-products` | `fromDate`, `toDate`. Aggregate in the database. |

Stubs currently return HTTP 501.

## Angular

Routes exist:

- `/login`
- `/products`
- `/orders`
- `/orders/create`
- `/orders/:id`

Complete login, token handling, HTTP authorization, route protection, and wiring screens to the API.

## Tests

The test project is ready. Write meaningful tests for critical order and security scenarios. Do not chase coverage percentage.

## Out of scope

Product CRUD, pixel-perfect UI, microservices, extra infrastructure, Dockerizing API/Angular.
