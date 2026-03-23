import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-status-badge',
  standalone: true,
  imports: [CommonModule],
  template: `<span [ngClass]="badgeClasses">{{ status }}</span>`,
})
export class StatusBadgeComponent {
  @Input() status = 'Unknown';

  get badgeClasses(): string[] {
    const base = ['inline-flex', 'items-center', 'px-2.5', 'py-0.5', 'rounded-full', 'text-xs', 'font-semibold'];
    switch (this.status?.toLowerCase()) {
      case 'running':
      case 'healthy':
      case 'active':
      case 'started':
      case 'created':
      case 'gitcloned':
        return [...base, 'bg-green-100', 'text-green-800', 'dark:bg-green-900/30', 'dark:text-green-400'];
      case 'stopped':
      case 'degraded':
      case 'suspended':
        return [...base, 'bg-yellow-100', 'text-yellow-800', 'dark:bg-yellow-900/30', 'dark:text-yellow-300'];
      case 'pending':
      case 'creating':
        return [...base, 'bg-cyan-100', 'text-cyan-800', 'dark:bg-cyan-900/30', 'dark:text-cyan-400'];
      case 'stopping':
      case 'destroying':
        return [...base, 'bg-orange-100', 'text-orange-800', 'dark:bg-orange-900/30', 'dark:text-orange-400'];
      case 'failed':
      case 'unreachable':
      case 'gitclonefailed':
        return [...base, 'bg-red-100', 'text-red-800', 'dark:bg-red-900/30', 'dark:text-red-400'];
      case 'destroyed':
      case 'archived':
        return [...base, 'bg-gray-100', 'text-gray-500', 'dark:bg-gray-800', 'dark:text-gray-500'];
      default:
        return [...base, 'bg-gray-100', 'text-gray-600', 'dark:bg-gray-700', 'dark:text-gray-400'];
    }
  }
}
