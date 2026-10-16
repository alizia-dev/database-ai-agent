import { ChangeDetectionStrategy, Component } from '@angular/core';

/** Public landing page. Phase 1 provides the hero; remaining sections are built with the public site. */
@Component({
  selector: 'app-landing-page',
  template: `
    <section class="hero">
      <h1>Talk to Your Business Database with AI</h1>
      <p>
        Connect your existing database, approve exactly which stored procedures the assistant may use, and ask
        questions in plain language. Your business data stays in your database.
      </p>
    </section>
  `,
  styles: `
    .hero {
      max-width: 760px;
      margin: 0 auto;
      padding: 96px 24px;
    }
    h1 {
      font: var(--mat-sys-display-small);
      margin: 0 0 16px;
    }
    p {
      font: var(--mat-sys-body-large);
      color: var(--mat-sys-on-surface-variant);
      margin: 0;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingPage {}
