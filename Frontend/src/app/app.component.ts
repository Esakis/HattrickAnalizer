import { Component } from '@angular/core';
import { TranslationService } from './services/translation.service';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.scss']
})
export class AppComponent {
  title = 'Frontend';

  constructor(public translationService: TranslationService) {}

  switchLanguage(lang: string): void {
    this.translationService.switchLanguage(lang);
  }

  getCurrentLanguage(): string {
    return this.translationService.getCurrentLanguage();
  }
}
