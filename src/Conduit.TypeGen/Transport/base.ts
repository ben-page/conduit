export interface DispatchResult<T> {
  data?: T;
  error?: ConduitError;
}

export interface ConduitError {
  code: string;
  message: string;
  details?: ConduitErrorDetail[];
  /** The error's HTTP-status category. 0 from a server that predates it. */
  status: number;
  /** A declared refusal's full type name; narrow with `isError`. */
  type?: string;
  data?: unknown;
  /** An unexpected failure's support reference; absent on refusals and from older servers. */
  reference?: string;
}

export interface ConduitErrorDetail {
  field?: string;
  message: string;
}

export interface ConduitTransport {
  sendRequest<TResult>(type: string, payload: unknown, headers?: Record<string, string>): Promise<DispatchResult<TResult>>;
  sendEvent(type: string, data: unknown, headers?: Record<string, string>): Promise<void>;
  subscribe(eventName: string, handler: (data: unknown) => void): Subscription;
}

export interface Subscription {
  unsubscribe(): void;
}
