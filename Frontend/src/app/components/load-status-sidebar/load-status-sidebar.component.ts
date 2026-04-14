import { Component } from '@angular/core';
import { Observable } from 'rxjs';
import { LoadItem, LoadStatusService } from '../../services/load-status.service';
import { TranslateService } from '@ngx-translate/core';

@Component({
  selector: 'app-load-status-sidebar',
  templateUrl: './load-status-sidebar.component.html',
  styleUrls: ['./load-status-sidebar.component.scss']
})
export class LoadStatusSidebarComponent {
  items$: Observable<LoadItem[]>;

  constructor(
    private loadStatus: LoadStatusService,
    private translate: TranslateService
  ) {
    this.items$ = this.loadStatus.items$;
  }

  icon(state: string): string {
    switch (state) {
      case 'loading': return '...';
      case 'success': return 'OK';
      case 'error': return 'XX';
      default: return 'o';
    }
  }

  getLabel(labelKey: string): string {
    return this.translate.instant(labelKey);
  }
}
