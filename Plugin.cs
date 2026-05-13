using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using IntroMarkerPlugin.Services;

namespace IntroMarkerPlugin;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IServerEntryPoint, IDisposable
{
    private readonly ILogger _logger;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepository;
    private readonly RuntimeState _runtimeState;
    private readonly DetectionCacheService _cacheService;
    private readonly MarkerService _markerService;
    private readonly FrameHashExtractor _frameHashExtractor;
    private readonly TheIntroDbService _theIntroDbService;
    private readonly SeasonDetectionService _seasonDetectionService;
    private readonly ScanCoordinator _scanCoordinator;
    private readonly ImportScanWatcher _importScanWatcher;
    private bool _disposed;

    public static Plugin? Instance { get; private set; }
    public static RuntimeState? Runtime { get; private set; }
    public static ScanCoordinator? Coordinator { get; private set; }
    public static DetectionCacheService? CacheService { get; private set; }

    public override string Name => "片头片尾识别";
    public override string Description => "为 Emby 识别电视剧片头片尾并写入标记。";
    public override Guid Id => Guid.Parse("4e3a9c85-90c7-4f32-8591-7a713c33a901");

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogManager logManager,
        ILibraryManager libraryManager,
        IItemRepository itemRepository) : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
        _logger = logManager.GetLogger(GetType().Name);

        _runtimeState = new RuntimeState();
        _cacheService = new DetectionCacheService(_applicationPaths, _logger, this);
        _markerService = new MarkerService(_itemRepository, _logger);
        _frameHashExtractor = new FrameHashExtractor(_logger, _cacheService, this);
        _theIntroDbService = new TheIntroDbService(_logger, _libraryManager);
        _seasonDetectionService = new SeasonDetectionService(_logger, _frameHashExtractor, _markerService, _theIntroDbService, this, _runtimeState);
        _scanCoordinator = new ScanCoordinator(_logger, _libraryManager, _seasonDetectionService, this, _runtimeState);
        _importScanWatcher = new ImportScanWatcher(_logger, _libraryManager, _scanCoordinator, this);

        Runtime = _runtimeState;
        Coordinator = _scanCoordinator;
        CacheService = _cacheService;
    }

    public override void SaveConfiguration()
    {
        NormalizeConfiguration(Configuration);
        base.SaveConfiguration();
        _logger.Info("片头片尾识别插件配置已更新");
    }

    public void Run()
    {
        NormalizeConfiguration(Configuration);
        _cacheService.Load();
        _importScanWatcher.Start();
        _logger.Info("片头片尾识别插件已启动");
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "IntroMarkerConfigurationV15",
                EmbeddedResourcePath = "IntroMarkerPlugin.Configuration.IntroMarkerConfiguration.html",
                DisplayName = "片头片尾识别",
                EnableInMainMenu = true,
                MenuIcon = "video_settings"
            },
            new PluginPageInfo
            {
                Name = "IntroMarkerConfigurationjsV15",
                EmbeddedResourcePath = "IntroMarkerPlugin.Configuration.IntroMarkerConfiguration.js"
            }
        };
    }

    public new static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    private static void NormalizeConfiguration(PluginConfiguration configuration)
    {
        configuration.EnsureDefaults();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _importScanWatcher.Dispose();
        _cacheService.Save();
    }
}
