using Xunit;

namespace Aura.Api.Tests;

public sealed class QueryOptimizationRegressionTests
{
    [Fact]
    public void MigrationReadme_ShouldMentionAllIncrementalSqlFiles()
    {
        var root = FindRepoRoot();
        var migrationsDir = Path.Combine(root, "database", "migrations");
        var readme = File.ReadAllText(Path.Combine(migrationsDir, "README.txt"));

        var migrationFiles = Directory.EnumerateFiles(migrationsDir, "*.sql")
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(migrationFiles);
        foreach (var fileName in migrationFiles)
        {
            Assert.Contains(fileName!, readme);
        }
    }

    [Fact]
    public void BaselineSchema_ShouldContainAlertSearchIndexes()
    {
        var root = FindRepoRoot();
        var schema = File.ReadAllText(Path.Combine(root, "database", "schema.pgsql.sql"));

        Assert.Contains("idx_alert_created_id_desc", schema);
        Assert.Contains("idx_alert_type_trgm", schema);
        Assert.Contains("idx_alert_detail_text_trgm", schema);
        Assert.Contains("gin_trgm_ops", schema);
    }

    [Fact]
    public void CaptureList_ShouldUseIndexedServerSideFilters()
    {
        var root = FindRepoRoot();
        var schema = File.ReadAllText(Path.Combine(root, "database", "schema.pgsql.sql"));
        var repository = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Data", "CaptureRepository.cs"));
        var service = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Capture", "CaptureOpsService.cs"));
        var exportService = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Export", "ExportApplicationService.cs"));
        var domainEndpoints = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Extensions", "AuraEndpointsDomain.cs"));
        var shell = File.ReadAllText(Path.Combine(root, "frontend", "common", "shell.js"));
        var script = File.ReadAllText(Path.Combine(root, "frontend", "capture", "capture.js"));
        var html = File.ReadAllText(Path.Combine(root, "frontend", "capture", "capture.html"));

        Assert.Contains("idx_capture_device_channel_time_desc", schema);
        Assert.Contains("device_id, channel_no, capture_time DESC, capture_id DESC", schema);
        Assert.Contains("long? deviceId = null", repository);
        Assert.Contains("int? channelNo = null", repository);
        Assert.Contains("AND device_id = @DeviceId", repository);
        Assert.Contains("AND channel_no = @ChannelNo", repository);
        Assert.Contains("httpReq.Query[\"deviceId\"]", service);
        Assert.Contains("httpReq.Query[\"channelNo\"]", service);
        Assert.Contains("ExportFilterOptions", exportService);
        Assert.Contains("GetCapturesPagedAsync(", exportService);
        Assert.Contains("long? deviceId = null", domainEndpoints);
        Assert.Contains("int? channelNo = null", domainEndpoints);
        Assert.Contains("options.params", shell);
        Assert.Contains("Object.entries(extraParams)", shell);
        Assert.Contains("appendCaptureFilters(query)", script);
        Assert.Contains("query.set(\"deviceId\"", script);
        Assert.Contains("query.set(\"channelNo\"", script);
        Assert.Contains("requestJson(`${apiBase}/api/capture/list?${query.toString()}`)", script);
        Assert.Contains("params: {", script);
        Assert.Contains("...getCaptureFilters()", script);
        Assert.Contains("window.aura?.requestJson || fallbackRequestJson", script);
        Assert.Contains("window.aura.openModal", script);
        Assert.Contains("bindModalDismiss(captureCreateModalEl", script);
        Assert.DoesNotContain("await fetch(`${apiBase}/api/capture/list", script);
        Assert.DoesNotContain("await fetch(`${apiBase}/api/capture/mock", script);
        Assert.Contains("id=\"captureDeviceIdFilter\"", html);
        Assert.Contains("id=\"captureChannelNoFilter\"", html);
        Assert.Contains("id=\"captureStartTimeFilter\"", html);
        Assert.Contains("id=\"captureEndTimeFilter\"", html);
    }

    [Fact]
    public void AlertPageScript_ShouldUseServerSidePagination()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "frontend", "alert", "alert.js"));
        var exportService = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Export", "ExportApplicationService.cs"));
        var domainEndpoints = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Extensions", "AuraEndpointsDomain.cs"));

        Assert.Contains("/api/alert/list?${query.toString()}", script);
        Assert.Contains("typeKeyword", script);
        Assert.Contains("detailKeyword", script);
        Assert.Contains("buildAlertExportParams()", script);
        Assert.Contains("params: buildAlertExportParams()", script);
        Assert.Contains("GetAlertsPagedAsync(", exportService);
        Assert.Contains("ApplyAlertFilters", exportService);
        Assert.Contains("string? typeKeyword = null", domainEndpoints);
        Assert.Contains("string? detailKeyword = null", domainEndpoints);
        Assert.DoesNotContain("limit=500", script);
        Assert.DoesNotContain("latestFilteredRows", script);
    }

    [Fact]
    public void LogQueries_ShouldUseTimeFiltersAndExportParams()
    {
        var root = FindRepoRoot();
        var schema = File.ReadAllText(Path.Combine(root, "database", "schema.pgsql.sql"));
        var repository = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Data", "AuditRepository.cs"));
        var operationService = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "OperationQueryService.cs"));
        var systemService = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "SystemLogQueryService.cs"));
        var exportService = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Export", "ExportApplicationService.cs"));
        var domainEndpoints = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Extensions", "AuraEndpointsDomain.cs"));
        var script = File.ReadAllText(Path.Combine(root, "frontend", "log", "log.js"));
        var html = File.ReadAllText(Path.Combine(root, "frontend", "log", "log.html"));

        Assert.Contains("idx_log_operation_created_id_desc", schema);
        Assert.Contains("idx_log_system_created_id_desc", schema);
        Assert.Contains("DateTimeOffset? from = null", repository);
        Assert.Contains("DateTimeOffset? to = null", repository);
        Assert.Contains("created_at >= @From", repository);
        Assert.Contains("created_at <= @To", repository);
        Assert.Contains("ORDER BY created_at DESC, op_id DESC", repository);
        Assert.Contains("ORDER BY created_at DESC, system_log_id DESC", repository);
        Assert.Contains("GetOperationsAsync(keyword, page, pageSize, from, to)", operationService);
        Assert.Contains("GetSystemLogsAsync(keyword, page, pageSize, from, to)", systemService);
        Assert.Contains("DateTimeOffset? from", domainEndpoints);
        Assert.Contains("DateTimeOffset? to", domainEndpoints);
        Assert.Contains("GetOperationsAsync(keyword, 1, maxRows, filters.From, filters.To)", exportService);
        Assert.Contains("GetSystemLogsAsync(keyword, 1, maxRows, filters.From, filters.To)", exportService);
        Assert.Contains("window.aura?.requestJson || fallbackRequestJson", script);
        Assert.Contains("appendLogFilters(query)", script);
        Assert.Contains("params: buildLogExportParams()", script);
        Assert.Contains("const result = await requestJson(`${apiBase}${endpoint}?${query.toString()}`)", script);
        Assert.DoesNotContain("await fetch(`${apiBase}${endpoint}", script);
        Assert.Contains("id=\"logStartTime\"", html);
        Assert.Contains("id=\"logEndTime\"", html);
    }

    [Fact]
    public void StatsPage_ShouldUseCachedBackendAndCommonRequestHelper()
    {
        var root = FindRepoRoot();
        var service = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "StatsApplicationService.cs"));
        var script = File.ReadAllText(Path.Combine(root, "frontend", "stats", "stats.js"));

        Assert.Contains("stats:overview:v1", service);
        Assert.Contains("stats:dashboard:v1", service);
        Assert.Contains("OverviewCacheTtl = TimeSpan.FromSeconds(15)", service);
        Assert.Contains("DashboardCacheTtl = TimeSpan.FromSeconds(30)", service);
        Assert.Contains("window.aura?.requestJson || fallbackRequestJson", script);
        Assert.Contains("requestJson(`${apiBase}/api/stats/overview`)", script);
        Assert.Contains("requestJson(`${apiBase}/api/stats/dashboard`)", script);
        Assert.DoesNotContain("fetch(`${apiBase}/api/stats/overview`", script);
        Assert.DoesNotContain("fetch(`${apiBase}/api/stats/dashboard`", script);
    }

    [Fact]
    public void SceneFeed_ShouldUseCommonRequestHelperAndIndexedTrackHistory()
    {
        var root = FindRepoRoot();
        var schema = File.ReadAllText(Path.Combine(root, "database", "schema.pgsql.sql"));
        var repository = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Data", "CaptureRepository.cs"));
        var script = File.ReadAllText(Path.Combine(root, "frontend", "scene", "scene.js"));

        Assert.Contains("idx_track_event_time_id_desc", schema);
        Assert.Contains("ON track_event(event_time DESC, event_id DESC)", schema);
        Assert.Contains("ORDER BY event_time DESC, event_id DESC", repository);
        Assert.Contains("PgSqlRepositoryHelpers.LogIfSlow(_logger, \"db query track events\"", repository);
        Assert.Contains("PgSqlRepositoryHelpers.LogIfSlow(_logger, \"db query track events in range\"", repository);
        Assert.Contains("window.aura?.requestJson || fallbackRequestJson", script);
        Assert.Contains("requestJson(`${apiBase}/api/floor/list`)", script);
        Assert.Contains("requestJson(`${apiBase}/api/camera/list`)", script);
        Assert.Contains("requestJson(`${apiBase}/api/capture/list?limit=24`)", script);
        Assert.Contains("requestJson(`${apiBase}/api/alert/list?limit=24`)", script);
        Assert.Contains("requestJson(`${apiBase}/api/track/history/list?limit=24`)", script);
        Assert.DoesNotContain("fetch(`${apiBase}/api/capture/list?limit=24`", script);
        Assert.DoesNotContain("fetch(`${apiBase}/api/alert/list?limit=24`", script);
        Assert.DoesNotContain("fetch(`${apiBase}/api/track/history/list?limit=24`", script);
    }

    [Fact]
    public void SearchPageScript_ShouldUseCommonFrontendHelpers()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "frontend", "search", "search.js"));

        Assert.Contains("window.aura?.requestJson || fallbackRequestJson", script);
        Assert.Contains("createStatusController", script);
        Assert.Contains("window.aura.setBusy", script);
        Assert.Contains("window.aura.openModal", script);
        Assert.Contains("bindModalDismiss(searchCompareModalEl", script);
        Assert.DoesNotContain("await fetch(`${apiBase}/api/vector/extract`", script);
        Assert.DoesNotContain("await fetch(`${apiBase}/api/vector/search`", script);
    }

    [Fact]
    public void SearchPageHtml_ShouldKeepReadableLabels()
    {
        var root = FindRepoRoot();
        var html = File.ReadAllText(Path.Combine(root, "frontend", "search", "search.html"));

        Assert.Contains("data-shell-title=\"\u4ee5\u56fe\u641c\u8f68\"", html);
        Assert.Contains("aria-label=\"\u4e3b\u5bfc\u822a\"", html);
        Assert.Contains("\u63d0\u7279\u5f81\u5e76\u68c0\u7d22", html);
        Assert.Contains("aria-label=\"\u4ee5\u56fe\u641c\u8f68\u7ed3\u679c\"", html);
        Assert.Contains("alt=\"\u547d\u4e2d\u56fe\"", html);
        Assert.DoesNotContain("alt=\"鍛", html);
        Assert.DoesNotContain("aria-label=\"灞", html);
    }

    [Fact]
    public void VectorSearch_ShouldUseShortRedisCache()
    {
        var root = FindRepoRoot();
        var service = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "VectorApplicationService.cs"));
        var registration = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Extensions", "AuraApplicationServiceRegistration.cs"));

        Assert.Contains("vector:extract:v1", service);
        Assert.Contains("vector:search:v1", service);
        Assert.Contains("ExtractCacheTtl = TimeSpan.FromSeconds(20)", service);
        Assert.Contains("SearchCacheTtl = TimeSpan.FromSeconds(20)", service);
        Assert.Contains("BuildExtractCacheKey(req.ImageBase64, metadataJson)", service);
        Assert.Contains("TryGetCachedExtractAsync(cacheKey)", service);
        Assert.Contains("SetCachedAsync(cacheKey, data, ExtractCacheTtl)", service);
        Assert.Contains("BuildSearchCacheKey(req.Feature, topK)", service);
        Assert.Contains("float.IsFinite", service);
        Assert.Contains("40073", service);
        Assert.Contains("TryGetCachedSearchAsync(cacheKey)", service);
        Assert.Contains("SetCachedAsync(cacheKey, data, SearchCacheTtl)", service);
        Assert.Contains("VectorExtractPayload", service);
        Assert.Contains("GetRequiredService<RedisCacheService>()", registration);
    }

    [Fact]
    public void AiInference_ShouldExposeBackpressureAndBatchMetrics()
    {
        var root = FindRepoRoot();
        var inference = File.ReadAllText(Path.Combine(root, "ai", "services", "inference_service.py"));
        var routeDeps = File.ReadAllText(Path.Combine(root, "ai", "app", "route_deps.py"));
        var tests = File.ReadAllText(Path.Combine(root, "ai", "tests", "test_ai_routes_and_index.py"));

        Assert.Contains("self._backpressure_total", inference);
        Assert.Contains("self._batch_total", inference);
        Assert.Contains("self._batch_error_total", inference);
        Assert.Contains("def inference_metrics(self) -> dict:", inference);
        Assert.Contains("\"enqueue_total\"", inference);
        Assert.Contains("\"backpressure_total\"", inference);
        Assert.Contains("\"processed_batches_total\"", inference);
        Assert.Contains("\"failed_batches_total\"", inference);
        Assert.Contains("self._record_backpressure()", inference);
        Assert.Contains("self._record_enqueue()", inference);
        Assert.Contains("self._record_batch_result(batch_size=len(batch)", inference);
        Assert.Contains("payload[\"inference_metrics\"] = self.inference.inference_metrics()", routeDeps);
        Assert.Contains("payload[\"inference_queue\"] = payload[\"inference_metrics\"][\"queue\"]", routeDeps);
        Assert.Contains("test_inference_metrics_track_backpressure_and_batch_results", tests);
        Assert.Contains("body[\"inference_metrics\"][\"queue\"][\"remaining\"]", tests);
    }

    [Fact]
    public void ExpansionFoundation_ShouldExposeWorkflowSpaceReportTenantAndAiPlatformContracts()
    {
        var root = FindRepoRoot();
        var schema = File.ReadAllText(Path.Combine(root, "database", "schema.pgsql.sql"));
        var migration = File.ReadAllText(Path.Combine(root, "database", "migrations", "014_add_workflow_space_report_tenant_ai_platform_tables.sql"));
        var requests = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Models", "Requests.cs"));
        var records = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Data", "PgSqlRecords.cs"));
        var repository = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Data", "ExtensionRepository.cs"));
        var registration = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Extensions", "AuraPersistenceServiceRegistration.cs"));
        var applicationRegistration = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Extensions", "AuraApplicationServiceRegistration.cs"));
        var permissions = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Internal", "AuraPermissions.cs"));
        var auth = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Extensions", "AuraAuthorizationExtensions.cs"));
        var endpoints = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Extensions", "AuraEndpointsDomain.cs"));
        var shell = File.ReadAllText(Path.Combine(root, "frontend", "common", "shell.js"));
        var extensionPage = File.ReadAllText(Path.Combine(root, "frontend", "extensions", "extensions.html"));
        var extensionScript = File.ReadAllText(Path.Combine(root, "frontend", "extensions", "extensions.js"));

        foreach (var table in new[]
        {
            "alert_workflow",
            "space_topology_edge",
            "space_heatmap_snapshot",
            "report_schedule",
            "report_run",
            "report_delivery",
            "tenant_project",
            "tenant_role_scope",
            "ai_provider_config",
            "ai_ab_experiment"
        })
        {
            Assert.Contains(table, schema);
            Assert.Contains(table, migration);
        }

        foreach (var index in new[]
        {
            "idx_alert_workflow_alert_updated",
            "idx_space_topology_from_camera",
            "idx_space_heatmap_floor_bucket",
            "idx_report_schedule_type_enabled",
            "idx_report_run_type_generated",
            "idx_report_delivery_role_status",
            "ux_tenant_role_scope",
            "idx_ai_provider_enabled_weight"
        })
        {
            Assert.Contains(index, schema);
            Assert.Contains(index, migration);
        }

        foreach (var contract in new[]
        {
            "AlertWorkflowUpdateReq",
            "SpaceTopologyEdgeReq",
            "SpaceHeatmapSnapshotReq",
            "ReportScheduleReq",
            "ReportGenerateReq",
            "TenantProjectReq",
            "TenantRoleScopeReq",
            "AiProviderConfigReq",
            "AiAbExperimentReq",
            "DbAlertWorkflow",
            "DbSpaceTopologyEdge",
            "DbSpaceHeatmapSnapshot",
            "DbReportSchedule",
            "DbReportRun",
            "DbReportDelivery",
            "DbTenantProject",
            "DbTenantRoleScope",
            "DbAiProviderConfig",
            "DbAiAbExperiment"
        })
        {
            Assert.True(requests.Contains(contract) || records.Contains(contract), $"{contract} should be declared.");
        }

        foreach (var method in new[]
        {
            "GetAlertWorkflowsAsync",
            "UpsertAlertWorkflowAsync",
            "CreateSpaceTopologyEdgeAsync",
            "CreateSpaceHeatmapAsync",
            "CreateReportScheduleAsync",
            "CreateReportRunAsync",
            "CreateReportDeliveryAsync",
            "CreateTenantAsync",
            "UpsertTenantRoleScopeAsync",
            "CreateAiProviderAsync",
            "CreateAiExperimentAsync"
        })
        {
            Assert.Contains(method, repository);
        }

        Assert.Contains("AddSingleton<ExtensionRepository>", registration);
        Assert.Contains("report.manage", permissions);
        Assert.Contains("space.manage", permissions);
        Assert.Contains("tenant.manage", permissions);
        Assert.Contains("ai.platform", permissions);
        Assert.Contains("AddPolicy(\"ReportManage\"", auth);
        Assert.Contains("AddPolicy(\"SpaceManage\"", auth);
        Assert.Contains("AddPolicy(\"TenantManage\"", auth);
        Assert.Contains("AddPolicy(\"AiPlatform\"", auth);

        Assert.Contains("/workflow/list", endpoints);
        Assert.Contains("/{alertId:long}/workflow", endpoints);
        Assert.Contains("/api/space", endpoints);
        Assert.Contains("/topology", endpoints);
        Assert.Contains("/heatmap", endpoints);
        Assert.Contains("/api/report", endpoints);
        Assert.Contains("/schedule/list", endpoints);
        Assert.Contains("/run/list", endpoints);
        Assert.Contains("/generate", endpoints);
        Assert.Contains("/api/tenant", endpoints);
        Assert.Contains("/scope/list", endpoints);
        Assert.Contains("/api/ai-platform", endpoints);
        Assert.Contains("RequireAuthorization(\"ReportManage\")", endpoints);
        Assert.Contains("RequireAuthorization(\"SpaceManage\")", endpoints);
        Assert.Contains("RequireAuthorization(\"TenantManage\")", endpoints);
        Assert.Contains("RequireAuthorization(\"AiPlatform\")", endpoints);

        Assert.Contains("/extensions/", shell);
        Assert.Contains("report.manage", shell);
        Assert.Contains("space.manage", shell);
        Assert.Contains("tenant.manage", shell);
        Assert.Contains("ai.platform", shell);
        var rolePage = File.ReadAllText(Path.Combine(root, "frontend", "role", "role.html"));
        var roleScript = File.ReadAllText(Path.Combine(root, "frontend", "role", "role.js"));
        Assert.Contains("value=\"report.manage\"", rolePage);
        Assert.Contains("value=\"space.manage\"", rolePage);
        Assert.Contains("value=\"tenant.manage\"", rolePage);
        Assert.Contains("value=\"ai.platform\"", rolePage);
        Assert.Contains("\"report.manage\": \"报表计划管理\"", roleScript);
        Assert.Contains("\"space.manage\": \"空间能力管理\"", roleScript);
        Assert.Contains("\"tenant.manage\": \"多租户管理\"", roleScript);
        Assert.Contains("\"ai.platform\": \"AI 平台管理\"", roleScript);
        Assert.Contains("extensions.html", extensionPage);
        Assert.Contains("/api/alert/workflow/list", extensionScript);
        Assert.Contains("/api/space/topology", extensionScript);
        Assert.Contains("/api/report/schedule/list", extensionScript);
        Assert.Contains("/api/report/run/list", extensionScript);
        Assert.Contains("/api/report/generate", extensionScript);
        Assert.Contains("/api/tenant/list", extensionScript);
        Assert.Contains("/api/tenant/scope/list", extensionScript);
        Assert.Contains("/api/tenant/scope", extensionScript);
        Assert.Contains("/api/ai-platform/providers", extensionScript);
        Assert.Contains("/api/ai-platform/experiments", extensionScript);
        Assert.Contains("window.aura?.renderTable", extensionScript);
        Assert.Contains("window.aura?.readForm", extensionScript);

        var reportAutomation = File.ReadAllText(Path.Combine(root, "backend", "Aura.Api", "Services", "ReportAutomationService.cs"));
        Assert.Contains("ReportAutomationHostedService", reportAutomation);
        Assert.Contains("RunDueSchedulesAsync", reportAutomation);
        Assert.Contains("GenerateAsync", reportAutomation);
        Assert.Contains("AddHostedService<ReportAutomationHostedService>", applicationRegistration);
        Assert.Contains("AddScoped<ReportAutomationService>", applicationRegistration);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "database", "schema.pgsql.sql")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
