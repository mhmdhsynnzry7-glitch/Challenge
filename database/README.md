# Database

Local development uses the installed default SQL Server instance with Windows Authentication. Start that service and use the existing Development connection:

```text
Server=.;Database=ChallengeDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True
```

The Windows account running the API needs permission to access/create `ChallengeDb`. Start the API with its HTTP Development launch profile; existing migrations and the original seed logic run automatically:

```bash
dotnet run --project backend/src/Challenge.Api --launch-profile http
```

Do not drop/reset existing data. No database other than `ChallengeDb` is needed.

The original Docker Compose setup remains available as an alternative, without being required for local development:

```bash
docker compose up -d
```

Original Docker/base connection (in `backend/src/Challenge.Api/appsettings.json`); Development overrides it with the local Windows-auth connection above:

```text
Server=localhost,1433;Database=ChallengeDb;User Id=sa;Password=Your_password123;TrustServerCertificate=True
```

Optional manual migrations, from the repository root, when a matching .NET 10 EF tool is available. Explicitly select Development in PowerShell so the local connection is used:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet ef database update --project backend/src/Challenge.Api --startup-project backend/src/Challenge.Api
```

If `dotnet ef` is missing:

```bash
dotnet tool install --global dotnet-ef --version 10.0.11
```

For the Docker alternative, override `ConnectionStrings__DefaultConnection` before starting the API. The backend integration tests intentionally require the local Windows-auth connection to `ChallengeDb` and clean up only their own test records.
