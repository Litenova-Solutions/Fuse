# SampleShop fixture

Small multi-project .NET sample used by golden-output and integration tests.

## Layout

- `SampleShop.Core`: shared library with an order/payment dependency cluster and planted secrets.
- `SampleShop.Web`: ASP.NET web app with MVC controllers and minimal API routes.

## Test scenarios

| Scenario | Seed / query | Expected behavior |
|----------|--------------|-------------------|
| Focus | `OrderService` | Includes `OrderService`, `PaymentService`, `PaymentGateway` (depth 2). |
| Query | `payment process` | BM25 ranks payment cluster over `CatalogItem`. |
| Secrets | default fusion | AWS key, JWT, and connection string are redacted. |
| Route map | `--route-map` | Lists controller actions and minimal API routes. |
| Project graph | `--project-graph` | Lists solution projects and Web -> Core reference. |
