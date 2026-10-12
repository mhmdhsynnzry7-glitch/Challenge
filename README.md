# Senior Full-Stack Technical Challenge

## Objective

Implement a small sales and inventory flow: login, list products, create an order, list/view orders, and a top-products report.

The goal is **not** to write as much code as possible. Focus on correctness, database consistency, security, and clear technical decisions.

## Technology

- ASP.NET Core Web API
- Entity Framework Core
- SQL Server
- Angular
- TypeScript
- JWT

## Time Limit

**Maximum: 3 hours**

## Prerequisites

- .NET SDK 10 (both backend projects target `net10.0`).
- Node.js 22.x and npm for Angular 19; see the [Angular compatibility table](https://angular.dev/reference/versions).
- A running local default SQL Server instance with Windows Authentication and permission to access/create `ChallengeDb`.
- Chrome for the existing Karma browser tests.

## Setup

1. Start your locally installed SQL Server service. Development uses the existing connection in `backend/src/Challenge.Api/appsettings.Development.json`:

```text
Server=.;Database=ChallengeDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True
```

Docker is optional for this local setup. The original Compose file is preserved as an alternative:

```bash
docker compose up -d
```

2. Restore/build the backend. The API applies existing pending migrations and the original seed logic on startup; a manual migration command is not required:

```bash
dotnet restore backend/Challenge.slnx
dotnet build backend/Challenge.slnx
```

3. Start backend:

```bash
dotnet run --project backend/src/Challenge.Api --launch-profile http
```

API: `http://localhost:5089`  
Swagger: `http://localhost:5089/swagger`

4. Start frontend:

```bash
cd frontend/challenge-app
npm ci
npm start
```

UI: `http://localhost:4200`

Use separate terminals for API and Angular; Ctrl+C stops each server. The HTTP launch profile selects Development and the local SQL connection. The frontend API URL is defined in `src/environments/environment.ts`, and backend CORS permits `http://localhost:4200`.

If using the optional Docker database instead, override `ConnectionStrings__DefaultConnection` with that instance's connection string. Development otherwise continues to target the installed SQL Server. Do not reset an existing database.

More database notes: `database/README.md`.

## Test Credentials

| Username | Password    | Role  |
| -------- | ----------- | ----- |
| `admin`  | `Admin123!` | Admin |
| `sales`  | `Sales123!` | Sales |

Passwords are stored as hashes. These credentials are for local evaluation only.

## Requirements

Implement at least:

- `POST /api/auth/login` — JWT login
- `GET /api/products` — search, active products, pagination
- `POST /api/orders` — create order and update stock
- `GET /api/orders` — search, filters, pagination, sorting (in the database)
- `GET /api/orders/{id}` — details with line prices from the order, not current product price
- `GET /api/reports/top-products` — SQL aggregation with `fromDate` / `toDate`

The existing Angular screens are implemented: Login, Products, Create Order, Orders, Order Details. The helper `GET /api/customers` provides active customer selection. API request/response details and date/sort defaults are documented in `NOTES.md`.

Business rules that must be enforced **on the backend**:

- Customer exists and is active
- Product exists and is active
- Quantity > 0
- Order has at least one item
- Stock is sufficient
- User is authenticated and authorized

Concurrency scenario (seeded product):

```text
LAPTOP-001 stock = 5
Request A quantity = 4
Request B quantity = 3
```

Both requests must not succeed. Stock must never become negative. A failed request must return an appropriate HTTP response.

## Constraints

You are not expected to implement every possible feature.

Prioritize correctness, security, business logic, concurrency, database consistency and code quality.

Product CRUD, rich UI, and Dockerizing the whole app are out of scope.

## Technical Decisions

Important technical decisions should be briefly documented (a short `NOTES.md` is enough).

## Verification

From the repository root, with local SQL Server running:

```bash
dotnet restore backend/Challenge.slnx
dotnet build backend/Challenge.slnx
dotnet test backend/Challenge.slnx
```

From `frontend/challenge-app`:

```bash
npm run build
npm test -- --watch=false --browsers=ChromeHeadless
```

If Chrome is not auto-detected, set `CHROME_BIN` to your installed Chrome executable before running tests. The existing backend tests use only the configured local `ChallengeDb`, isolate their own data, and restore it after execution. Run these integration tests without other writes to the same database. The inventory test synchronizes two independent SQL connections for the stock-5 / quantities-4-and-3 scenario.

## Submission notes

Keep source, project files, lockfiles, original challenge documents, migrations, seed logic, and Docker Compose. Generated `bin`, `obj`, `node_modules`, `.angular`, `dist`, IDE files, logs, and office lock files are excluded by ignore rules and are not submission source. No E2E runner is configured under `ng e2e`; browser checks are reported separately from Karma tests.

The existing NU1510 redundant-reference warning does not block restore/build/tests. List counts and page rows are separate SQL reads; concurrent changes can alter results between them. UI order totals are estimates; saved server amounts are authoritative. Production credentials and JWT signing keys must come from configuration outside the development defaults.
