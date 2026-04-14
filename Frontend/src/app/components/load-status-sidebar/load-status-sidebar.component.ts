import { Component } from '@angular/core';
import { Observable } from 'rxjs';
import { LoadItem, LoadStatusService } from '../../services/load-status.service';

@Component({
  selector: 'app-load-status-sidebar',
  templateUrl: './load-status-sidebar.component.html',
  styleUrls: ['./load-status-sidebar.component.scss']
})
export class LoadStatusSidebarComponent {
  items$: Observable<LoadItem[]>;

  constructor(private loadStatus: LoadStatusService) {
    this.items$ = this.loadStatus.items$;
  }

  icon(state: string): string {
    switch (state) {
      case 'loading': return '⏳';
      case 'success': return '✅';
      case 'error': return '❌';
      default: return '⚪';
    }
  }
}
