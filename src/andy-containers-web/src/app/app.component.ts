import { Component } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css',
})
export class AppComponent {
  title = 'Andy Containers';
  sidebarOpen = false;
  darkMode = false;

  toggleSidebar(): void {
    this.sidebarOpen = !this.sidebarOpen;
  }

  toggleDarkMode(): void {
    this.darkMode = !this.darkMode;
    document.documentElement.classList.toggle('dark', this.darkMode);
  }

  navItems = [
    { label: 'Dashboard', route: '/dashboard', icon: '\u2302' },
    { label: 'Containers', route: '/containers', icon: '\u25A3' },
    { label: 'Templates', route: '/templates', icon: '\u2630' },
    { label: 'Providers', route: '/providers', icon: '\u2601' },
    { label: 'Workspaces', route: '/workspaces', icon: '\u2616' },
  ];
}
