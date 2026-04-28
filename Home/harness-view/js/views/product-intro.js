/**
 * Product Intro — embeds Home/index.html (the public landing page) inside the
 * harness view. Single iframe that fills the right pane; the sidebar already
 * provides the left navigation.
 */
import { h, mount } from '../utils/dom.js';
import { renderTopBar } from './_common.js';

export async function render(ctx) {
  const { viewEl, topbarEl } = ctx;

  renderTopBar(topbarEl, {
    title: 'Product Intro',
    subtitle: 'Home/index.html — public landing embedded inside the harness',
    badge: { kind: 'readonly', text: 'Live' },
    extra: h('a', {
      class: 'btn',
      href: '../index.html',
      target: '_blank',
      rel: 'noopener',
    }, 'Open in new tab ↗'),
  });

  // Home/harness-view/  →  ../index.html  resolves to Home/index.html
  const frame = h('iframe', {
    src: '../index.html',
    title: 'AgentZero Lite — Product Intro',
    style: {
      width: '100%',
      height: 'calc(100vh - 120px)',   // viewport minus top + sub bars
      minHeight: '600px',
      border: '1px solid #E5E7EB',
      borderRadius: '8px',
      background: '#FFFFFF',
    },
  });

  mount(viewEl, frame);
}
