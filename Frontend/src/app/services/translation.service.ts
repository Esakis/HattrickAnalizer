import { Injectable } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';

@Injectable({
  providedIn: 'root'
})
export class TranslationService {
  private currentLang: string = 'pl';

  constructor(private translate: TranslateService) {
    this.translate.setDefaultLang('pl');
    const savedLang = localStorage.getItem('selectedLanguage');
    if (savedLang) {
      this.currentLang = savedLang;
      this.translate.use(savedLang);
    } else {
      this.translate.use('pl');
    }
  }

  switchLanguage(lang: string): void {
    this.currentLang = lang;
    this.translate.use(lang);
    localStorage.setItem('selectedLanguage', lang);
  }

  getCurrentLanguage(): string {
    return this.currentLang;
  }

  getAvailableLanguages(): string[] {
    return ['pl', 'en'];
  }
}
