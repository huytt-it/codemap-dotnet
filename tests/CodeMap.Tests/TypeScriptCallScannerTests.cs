using CodeMap.Query.FrontendScan;

namespace CodeMap.Tests;

/// <summary>
/// ts-call-scan.js: which receivers count as an Angular HttpClient, and which classes count as injecting a
/// service. Every test writes a throw-away TypeScript project and, like InjectedByTests, skips (Inconclusive)
/// rather than fails when `node` or a resolvable `typescript` package is missing.
/// </summary>
[TestClass]
public class TypeScriptCallScannerTests
{
    [TestMethod] // the receiver's name says nothing ("api"), its declared type does
    public void Constructor_parameter_typed_HttpClient_is_a_receiver_whatever_its_name()
    {
        var calls = Scan(("src/app/order-api.service.ts",
            "import { HttpClient } from '@angular/common/http';\n@Injectable()\nexport class OrderApiService {\n  constructor(private api: HttpClient) {}\n  list() { return this.api.get('/api/orders'); }\n}\n"));

        var call = calls.Single();
        Assert.AreEqual("GET", call.HttpMethod);
        Assert.AreEqual("'/api/orders'", call.RawUrl);
    }

    [TestMethod]
    public void Field_initialised_with_inject_HttpClient_is_a_receiver()
    {
        var calls = Scan(("src/app/order-api.service.ts",
            "import { HttpClient, inject } from '@angular/common/http';\n@Injectable({ providedIn: 'root' })\nexport class OrderApiService {\n  private readonly client = inject(HttpClient);\n  create(body: unknown) { return this.client.post('/api/orders', body); }\n}\n"));

        Assert.AreEqual("POST", calls.Single().HttpMethod);
    }

    [TestMethod]
    public void Field_with_an_HttpClient_type_annotation_is_a_receiver()
    {
        var calls = Scan(("src/app/order-api.service.ts",
            "@Injectable()\nexport class OrderApiService {\n  protected rest: HttpClient;\n  constructor(rest: HttpClient) { this.rest = rest; }\n  remove(id: number) { return this.rest.delete(`/api/orders/${id}`); }\n}\n"));

        Assert.AreEqual("DELETE", calls.Single().HttpMethod);
    }

    [TestMethod]
    public void Local_variable_and_function_parameter_typed_HttpClient_are_receivers()
    {
        var calls = Scan(("src/app/orders.ts",
            "export function loadOrders(client: HttpClient) {\n  return client.get('/api/orders');\n}\nexport function loadCustomers() {\n  const backend = inject(HttpClient);\n  return backend.get('/api/customers');\n}\n"));

        CollectionAssert.AreEquivalent(new[] { "'/api/orders'", "'/api/customers'" }, calls.Select(c => c.RawUrl).ToList());
    }

    [TestMethod] // `import { HttpClient as Backend }` renames the type, not what it is
    public void Aliased_HttpClient_import_is_recognised()
    {
        var calls = Scan(("src/app/order-api.service.ts",
            "import { HttpClient as Backend } from '@angular/common/http';\n@Injectable()\nexport class OrderApiService {\n  constructor(private api: Backend) {}\n  list() { return this.api.get('/api/orders'); }\n}\n"));

        Assert.AreEqual(1, calls.Count);
    }

    [TestMethod] // the declaring class is in another file: only its `extends` chain says that `this.api` is an HttpClient
    public void Receiver_inherited_from_a_base_class_in_another_file_is_recognised()
    {
        var calls = Scan(
            ("src/app/base-api.service.ts", "export abstract class BaseApiService {\n  constructor(protected api: HttpClient) {}\n}\n"),
            ("src/app/order-api.service.ts", "import { BaseApiService } from './base-api.service';\nexport class OrderApiService extends BaseApiService {\n  list() { return this.api.get('/api/orders'); }\n}\n"));

        Assert.AreEqual("src/app/order-api.service.ts", calls.Single().File);
    }

    [TestMethod] // precision: .get(...) on a FormGroup, a Map, a router param map is not an HTTP call just because the method is called get
    public void Receivers_not_declared_as_HttpClient_are_not_http_calls()
    {
        var calls = Scan(("src/app/order-form.component.ts",
            "@Component({ selector: 'app-order-form' })\nexport class OrderFormComponent {\n  private cache = new Map<string, string>();\n  constructor(private form: FormGroup, private route: ActivatedRoute) {}\n  read() {\n    this.form.get('customer/name');\n    this.cache.get('orders/last');\n    this.route.snapshot.paramMap.get('orders/id');\n    window.localStorage.getItem('x/y');\n  }\n}\n"));

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod] // a name that happens to be declared HttpClient in ANOTHER class must not make the same name an HttpClient here
    public void HttpClient_member_name_is_resolved_per_class()
    {
        var calls = Scan(("src/app/mixed.ts",
            "export class ApiService {\n  constructor(private api: HttpClient) {}\n  list() { return this.api.get('/api/orders'); }\n}\nexport class SettingsStore {\n  private api = new Map<string, string>();\n  read() { return this.api.get('settings/theme'); }\n}\n"));

        Assert.AreEqual("'/api/orders'", calls.Single().RawUrl);
    }

    [TestMethod] // Angular 14+: `private orderApi = inject(OrderApiService)` instead of a constructor parameter
    public void Component_that_injects_a_service_through_a_field_is_listed_as_injecting_it()
    {
        var calls = Scan(
            ("src/app/orders/order-api.service.ts", "@Injectable({ providedIn: 'root' })\nexport class OrderApiService {\n  constructor(private http: HttpClient) {}\n  cancel(id: number) { return this.http.delete(`/api/orders/${id}`); }\n}\n"),
            ("src/app/orders/order-list.component.ts", "@Component({ selector: 'app-order-list' })\nexport class OrderListComponent {\n  private orderApi = inject(OrderApiService);\n}\n"),
            ("src/app/orders/order-admin.component.ts", "@Component({ selector: 'app-order-admin' })\nexport class OrderAdminComponent {\n  protected readonly orders: OrderApiService = inject(OrderApiService, { optional: true })!;\n}\n"),
            ("src/app/orders/order-old.component.ts", "@Component({ selector: 'app-order-old' })\nexport class OrderOldComponent {\n  constructor(private orderApi: OrderApiService) {}\n}\n"));

        var call = calls.Single();
        CollectionAssert.AreEquivalent(new[] { "OrderListComponent", "OrderAdminComponent", "OrderOldComponent" }, call.InjectedBy);
    }

    [TestMethod] // a component that both takes a service in its constructor and via inject() is still listed once
    public void Component_injecting_the_same_service_twice_is_listed_once()
    {
        var calls = Scan(
            ("src/app/order-api.service.ts", "@Injectable()\nexport class OrderApiService {\n  constructor(private http: HttpClient) {}\n  list() { return this.http.get('/api/orders'); }\n}\n"),
            ("src/app/order-list.component.ts", "@Component({ selector: 'a' })\nexport class OrderListComponent {\n  private again = inject(OrderApiService);\n  constructor(private orderApi: OrderApiService) {}\n}\n"));

        CollectionAssert.AreEqual(new[] { "OrderListComponent" }, calls.Single().InjectedBy);
    }

    [TestMethod] // inject() of something that is not a class token (a string, a call) names no service
    public void Inject_of_a_non_class_token_does_not_register_an_injection()
    {
        var calls = Scan(
            ("src/app/order-api.service.ts", "@Injectable()\nexport class OrderApiService {\n  constructor(private http: HttpClient) {}\n  list() { return this.http.get('/api/orders'); }\n}\n"),
            ("src/app/order-list.component.ts", "@Component({ selector: 'a' })\nexport class OrderListComponent {\n  private token = inject(getToken());\n  private other = notInject(OrderApiService);\n}\n"));

        Assert.AreEqual(0, calls.Single().InjectedBy.Count);
    }

    private static List<TypeScriptCallScanner.RawCall> Scan(params (string Path, string Content)[] files)
    {
        var root = TestPaths.NewTempDir();
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        var outcome = TypeScriptCallScanner.Scan(root);
        if (outcome.SkippedReason != null)
            Assert.Inconclusive($"TypeScript scanning unavailable in this environment: {outcome.SkippedReason}");
        return outcome.Calls;
    }
}
