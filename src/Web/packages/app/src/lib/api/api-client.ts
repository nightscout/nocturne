import {
  Client,
  AuthenticationClient,
  AuthorizationClient,
  StatisticsClient,
  VersionsClient,
  DeviceStatusClient,
  EntriesClient,
  FoodClient,
  LastModifiedClient,
  ProfileClient,
  SettingsClient,
  StatusClient,
  TreatmentsClient,
  VersionClient,
  DDataClient,
  LoopClient,
  PropertiesClient,
  SummaryClient,
  ActivityClient,
  AlexaClient,
  CountClient,
  DeviceAgeClient,
  EchoClient,
  TimeQueryClient,
  NotificationsClient,
  IobClient,
  CompatibilityClient,
} from "./generated/nocturne-api-client";

/**
 * API client wrapper for the Nocturne API This wrapper provides a convenient
 * interface to the auto-generated NSwag client
 */
export class ApiClient {
  public readonly client: Client;
  public readonly authentication: AuthenticationClient;
  public readonly authorization: AuthorizationClient;
  public readonly statistics: StatisticsClient;
  public readonly versions: VersionsClient;
  public readonly deviceStatus: DeviceStatusClient;
  public readonly entries: EntriesClient;
  public readonly food: FoodClient;
  public readonly lastModified: LastModifiedClient;
  public readonly profile: ProfileClient;
  public readonly settings: SettingsClient;
  public readonly status: StatusClient;
  public readonly treatments: TreatmentsClient;
  public readonly version: VersionClient;
  public readonly v2DData: DDataClient;
  public readonly loopNotifications: LoopClient;
  public readonly v2Notifications: NotificationsClient;
  public readonly v2Properties: PropertiesClient;
  public readonly v2Summary: SummaryClient;
  public readonly activity: ActivityClient;
  public readonly alexa: AlexaClient;
  public readonly count: CountClient;
  public readonly deviceAge: DeviceAgeClient;
  public readonly echo: EchoClient;
  public readonly v1Notifications: NotificationsClient;
  public readonly timeQuery: TimeQueryClient;
  public readonly iob: IobClient;
  public readonly compatibility: CompatibilityClient ;

  constructor(
    baseUrl: string,
    http?: { fetch(url: RequestInfo, init?: RequestInit): Promise<Response> }
  ) {
    const apiBaseUrl = baseUrl;

    if (!apiBaseUrl) {
      throw new Error(
        "API base URL is not defined. Please set it in your environment variables."
      );
    }

    // Initialize all client instances with the custom fetch function
    this.client = new Client(apiBaseUrl, http);
    this.authentication = new AuthenticationClient(apiBaseUrl, http);
    this.authorization = new AuthorizationClient(apiBaseUrl, http);
    this.statistics = new StatisticsClient(apiBaseUrl, http);
    this.versions = new VersionsClient(apiBaseUrl, http);
    this.deviceStatus = new DeviceStatusClient(apiBaseUrl, http);
    this.entries = new EntriesClient(apiBaseUrl, http);
    this.food = new FoodClient(apiBaseUrl, http);
    this.lastModified = new LastModifiedClient(apiBaseUrl, http);
    this.profile = new ProfileClient(apiBaseUrl, http);
    this.settings = new SettingsClient(apiBaseUrl, http);
    this.status = new StatusClient(apiBaseUrl, http);
    this.treatments = new TreatmentsClient(apiBaseUrl, http);
    this.version = new VersionClient(apiBaseUrl, http);
    this.v2DData = new DDataClient(apiBaseUrl, http);
    this.loopNotifications = new LoopClient(apiBaseUrl, http);
    this.v2Notifications = new NotificationsClient(apiBaseUrl, http);
    this.v2Properties = new PropertiesClient(apiBaseUrl, http);
    this.v2Summary = new SummaryClient(apiBaseUrl, http);
    this.activity = new ActivityClient(apiBaseUrl, http);
    this.alexa = new AlexaClient(apiBaseUrl, http);
    this.count = new CountClient(apiBaseUrl, http);
    this.deviceAge = new DeviceAgeClient(apiBaseUrl, http);
    this.echo = new EchoClient(apiBaseUrl, http);
    this.v1Notifications = new NotificationsClient(apiBaseUrl, http);
    this.timeQuery = new TimeQueryClient(apiBaseUrl, http);
    this.iob = new IobClient(apiBaseUrl, http);
    this.compatibility = new CompatibilityClient (apiBaseUrl, http);
  }

  /** Get the underlying main client for direct access if needed */
  get rawClient(): Client {
    return this.client;
  }
}

// Export the generated client types for use in components
export * from "./generated/nocturne-api-client";
