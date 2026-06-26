# OrderingApp fixture

A hermetic semantic fixture exercising every Fuse analyzer. It compiles in-memory from source (no NuGet
restore) via `OrderingAppFixture.Load()` in `Fuse.Semantics.Tests`; `Framework.cs` provides minimal
stand-ins for MediatR, ASP.NET Core MVC, and `Microsoft.Extensions.*` in their real namespaces so detection
behaves as it would against the real packages.

Expected semantic edges (the spine of the Phase 4 analyzer tests):

```text
IOrderService            -> OrderService            : di_resolves_to
OrdersController          -> IOrderService           : di_injects
OrdersController          -> OrderService            : di_depends_on_impl
CreateOrderCommand       -> CreateOrderHandler       : mediatr_handles
POST /api/orders/{id}    -> OrdersController.Create  : route_handles
Orders (config)          -> OrderOptions             : options_binds
OrderService             -> OrderOptions             : options_consumes
OrderServiceTests        -> OrderService             : tests
```
