import { Component } from '@angular/core';
import { HattrickApiService } from '../../services/hattrick-api.service';

@Component({
  selector: 'app-oauth-setup',
  templateUrl: './oauth-setup.component.html',
  styleUrls: ['./oauth-setup.component.scss']
})
export class OAuthSetupComponent {
  step: number = 1;
  sessionId: string = '';
  authorizationUrl: string = '';
  verifier: string = '';
  loading: boolean = false;
  error: string | null = null;
  success: boolean = false;

  constructor(private hattrickApi: HattrickApiService) {}

  async startAuthorization(): Promise<void> {
    this.loading = true;
    this.error = null;

    try {
      const response = await this.hattrickApi.startOAuthFlow().toPromise();
      this.sessionId = response.sessionId;
      this.authorizationUrl = response.authorizationUrl;
      this.step = 2;
    } catch (err: any) {
      this.error = 'Błąd podczas inicjalizacji OAuth: ' + err.message;
    } finally {
      this.loading = false;
    }
  }

  openAuthUrl(): void {
    window.open(this.authorizationUrl, '_blank');
  }

  async completeAuthorization(): Promise<void> {
    if (!this.verifier) {
      this.error = 'Wpisz PIN z Hattrick';
      return;
    }

    this.loading = true;
    this.error = null;

    try {
      const response = await this.hattrickApi.completeOAuthFlow(this.sessionId, this.verifier).toPromise();
      this.success = true;
      this.step = 3;
      
      localStorage.setItem('hattrick_session_id', this.sessionId);
    } catch (err: any) {
      this.error = 'Błąd podczas finalizacji OAuth: ' + err.message;
    } finally {
      this.loading = false;
    }
  }

  reset(): void {
    this.step = 1;
    this.sessionId = '';
    this.authorizationUrl = '';
    this.verifier = '';
    this.error = null;
    this.success = false;
  }

  copyToClipboard(text: string): void {
    navigator.clipboard.writeText(text).then(() => {
      alert('Skopiowano do schowka!');
    });
  }
}
