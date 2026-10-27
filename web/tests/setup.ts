// Browser APIs that jsdom does not implement but the app uses. They do nothing: the tests check behaviour, not layout.
if (!HTMLElement.prototype.scrollTo) HTMLElement.prototype.scrollTo = function scrollTo() {};
if (!HTMLElement.prototype.scrollIntoView) HTMLElement.prototype.scrollIntoView = function scrollIntoView() {};

if (!('ResizeObserver' in window)) {
  (window as unknown as { ResizeObserver: unknown }).ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
}

if (!window.matchMedia) {
  window.matchMedia = ((query: string) => ({
    matches: false, media: query, onchange: null,
    addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}
