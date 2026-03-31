import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';

@Component({
  selector: 'app-docs',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="docs-container">
      <iframe [src]="docsUrl" class="docs-iframe" title="Documentation"></iframe>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      margin: -2rem;
      width: calc(100% + 4rem);
      height: calc(100vh - 64px);
      overflow: hidden;
    }
    .docs-container {
      width: 100%;
      height: 100%;
    }
    .docs-iframe {
      width: 100%;
      height: 100%;
      border: none;
    }
  `],
})
export class DocsComponent {
  docsUrl: SafeResourceUrl;

  constructor(private sanitizer: DomSanitizer) {
    // In dev: MkDocs serves at localhost:8000
    // In docker/prod: served as static files at /docs/ or external URL
    const baseUrl = window.location.hostname === 'localhost'
      ? 'http://localhost:8000/andy-containers/'
      : '/docs/';
    this.docsUrl = this.sanitizer.bypassSecurityTrustResourceUrl(baseUrl);
  }
}
