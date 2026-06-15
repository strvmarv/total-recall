import '@testing-library/jest-dom';

// Recharts' ResponsiveContainer relies on ResizeObserver, absent in jsdom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;

// jsdom has no matchMedia. The Phase 1 inline script in index.html uses it to
// resolve the system-preference default; this stub prevents hard crashes if any
// test indirectly exercises that path. useTheme itself does NOT call matchMedia.
if (!('matchMedia' in window)) {
  (window as unknown as { matchMedia: unknown }).matchMedia = (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener() {},
    removeEventListener() {},
    addListener() {},
    removeListener() {},
    dispatchEvent() { return false; },
  });
}

// jsdom does not implement Element.prototype.scrollIntoView; CommandPalette
// calls it to keep the active option visible during keyboard navigation.
if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = () => {};
}
