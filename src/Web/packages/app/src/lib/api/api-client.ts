import {
  Client,
  AuthenticationClient,
  AuthorizationClient,
  StatisticsClient,
  VersionsClient,
  DeviceStatusClient,
  EntriesClient,
  FoodClient,
  FoodsClient,
  LastModifiedClient,
  ProfileClient,
  SettingsClient,
  StatusClient,
  TreatmentsClient,
  TreatmentFoodsClient,
  VersionClient,
  DDataClient,
  LoopClient,
  PropertiesClient,
  SummaryClient,
  ActivityClient,
  AlexaClient,
  CountClient,
  TrackersClient,
  TimeQueryClient,
  NotificationsClient,
  IobClient,
  CompatibilityClient,
  DiscrepancyClient,
  UISettingsClient,
  ServicesClient,
  LocalAuthClient,
  OidcClient,
  BatteryClient,
  PredictionClient,
  RetrospectiveClient,
  ConnectorStatusClient,
  MetadataClient,
  ChartDataClient,
  StateSpansClient,
  SystemEventsClient,
  MigrationClient,
  DeduplicationClient,
  ConfigurationClient,
  MealMatchingClient,
  ClockFacesClient
} from "./generated/nocturne-api-client";

/**
 * API client wrapper for the Nocturne API
 *
 * This wrapper provides a convenient interface to the auto-generated NSwag client. It must be manually updated with any endpoints that are added.
 */
export class ApiClient {
  public readonly baseUrl: string;
  public readonly client: Client;
  public readonly authentication: AuthenticationClient;
  public readonly authorization: AuthorizationClient;
  public readonly statistics: StatisticsClient;
  public readonly versions: VersionsClient;
  public readonly deviceStatus: DeviceStatusClient;
  public readonly entries: EntriesClient;
  public readonly food: FoodClient;
  public readonly foodsV4: FoodsClient;
  public readonly lastModified: LastModifiedClient;
  public readonly profile: ProfileClient;
  public readonly settings: SettingsClient;
  public readonly status: StatusClient;
  public readonly treatments: TreatmentsClient;
  public readonly treatmentFoods: TreatmentFoodsClient;
  public readonly version: VersionClient;
  public readonly v2DData: DDataClient;
  public readonly loopNotifications: LoopClient;
  public readonly v2Notifications: NotificationsClient;
  public readonly v2Properties: PropertiesClient;
  public readonly v2Summary: SummaryClient;
  public readonly activity: ActivityClient;
  public readonly alexa: AlexaClient;
  public readonly count: CountClient;
  public readonly trackers: TrackersClient;

  public readonly v1Notifications: NotificationsClient;
  public readonly timeQuery: TimeQueryClient;
  public readonly iob: IobClient;
  public readonly compatibility: CompatibilityClient;
  public readonly discrepancy: DiscrepancyClient;
  public readonly uiSettings: UISettingsClient;
  public readonly services: ServicesClient;
  public readonly localAuth: LocalAuthClient;
  public readonly oidc: OidcClient;
  public readonly battery: BatteryClient;
  public readonly predictions: PredictionClient;
  public readonly retrospective: RetrospectiveClient;
  public readonly connectorStatus: ConnectorStatusClient;
  public readonly metadata: MetadataClient;
  public readonly chartData: ChartDataClient;
  public readonly stateSpans: StateSpansClient;
  public readonly systemEvents: SystemEventsClient;
  public readonly migration: MigrationClient;
  public readonly deduplication: DeduplicationClient;
  public readonly configuration: ConfigurationClient;
  public readonly mealMatching: MealMatchingClient;
  public readonly clockFaces: ClockFacesClient;

  constructor(
    baseUrl: string,
    http?: { fetch(url: RequestInfo, init?: RequestInit): Promise<Response> }
  ) {
    const apiBaseUrl = baseUrl;
    this.baseUrl = apiBaseUrl;

    // Initialize all client instances with the custom fetch function
    this.client = new Client(apiBaseUrl, http);
    this.authentication = new AuthenticationClient(apiBaseUrl, http);
    this.authorization = new AuthorizationClient(apiBaseUrl, http);
    this.statistics = new StatisticsClient(apiBaseUrl, http);
    this.versions = new VersionsClient(apiBaseUrl, http);
    this.deviceStatus = new DeviceStatusClient(apiBaseUrl, http);
    this.entries = new EntriesClient(apiBaseUrl, http);
    this.food = new FoodClient(apiBaseUrl, http);
    this.foodsV4 = new FoodsClient(apiBaseUrl, http);
    this.lastModified = new LastModifiedClient(apiBaseUrl, http);
    this.profile = new ProfileClient(apiBaseUrl, http);
    this.settings = new SettingsClient(apiBaseUrl, http);
    this.status = new StatusClient(apiBaseUrl, http);
    this.treatments = new TreatmentsClient(apiBaseUrl, http);
    this.treatmentFoods = new TreatmentFoodsClient(apiBaseUrl, http);
    this.version = new VersionClient(apiBaseUrl, http);
    this.v2DData = new DDataClient(apiBaseUrl, http);
    this.loopNotifications = new LoopClient(apiBaseUrl, http);
    this.v2Notifications = new NotificationsClient(apiBaseUrl, http);
    this.v2Properties = new PropertiesClient(apiBaseUrl, http);
    this.v2Summary = new SummaryClient(apiBaseUrl, http);
    this.activity = new ActivityClient(apiBaseUrl, http);
    this.alexa = new AlexaClient(apiBaseUrl, http);
    this.count = new CountClient(apiBaseUrl, http);
    this.trackers = new TrackersClient(apiBaseUrl, http);
    this.v1Notifications = new NotificationsClient(apiBaseUrl, http);
    this.timeQuery = new TimeQueryClient(apiBaseUrl, http);
    this.iob = new IobClient(apiBaseUrl, http);
    this.compatibility = new CompatibilityClient(apiBaseUrl, http);
    this.discrepancy = new DiscrepancyClient(apiBaseUrl, http);
    this.uiSettings = new UISettingsClient(apiBaseUrl, http);
    this.services = new ServicesClient(apiBaseUrl, http);
    this.localAuth = new LocalAuthClient(apiBaseUrl, http);
    this.oidc = new OidcClient(apiBaseUrl, http);
    this.battery = new BatteryClient(apiBaseUrl, http);
    this.predictions = new PredictionClient(apiBaseUrl, http);
    this.retrospective = new RetrospectiveClient(apiBaseUrl, http);
    this.connectorStatus = new ConnectorStatusClient(apiBaseUrl, http);
    this.metadata = new MetadataClient(apiBaseUrl, http);
    this.chartData = new ChartDataClient(apiBaseUrl, http);
    this.stateSpans = new StateSpansClient(apiBaseUrl, http);
    this.systemEvents = new SystemEventsClient(apiBaseUrl, http);
    this.migration = new MigrationClient(apiBaseUrl, http);
    this.deduplication = new DeduplicationClient(apiBaseUrl, http);
    this.configuration = new ConfigurationClient(apiBaseUrl, http);
    this.mealMatching = new MealMatchingClient(apiBaseUrl, http);
    this.clockFaces = new ClockFacesClient(apiBaseUrl, http);
  }

  /** Get the underlying main client for direct access if needed */
  get rawClient(): Client {
    return this.client;
  }
}

// Export the generated client types for use in components
export * from "./generated/nocturne-api-client";
