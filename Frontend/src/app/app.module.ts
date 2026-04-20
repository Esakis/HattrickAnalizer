import { NgModule } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';
import { HttpClientModule, HttpClient, HTTP_INTERCEPTORS } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { TranslateModule, TranslateLoader } from '@ngx-translate/core';
import { Observable } from 'rxjs';

import { AppRoutingModule } from './app-routing.module';
import { AppComponent } from './app.component';
import { LineupOptimizerComponent } from './components/lineup-optimizer/lineup-optimizer.component';
import { OAuthSetupComponent } from './components/oauth-setup/oauth-setup.component';
import { PlayersComponent } from './components/players/players.component';
import { LoadStatusSidebarComponent } from './components/load-status-sidebar/load-status-sidebar.component';
import { PlayerHistoryModalComponent } from './components/player-history-modal/player-history-modal.component';
import { CredentialsInterceptor } from './interceptors/credentials.interceptor';

export class CustomTranslateLoader implements TranslateLoader {
  constructor(private http: HttpClient) {}

  getTranslation(lang: string): Observable<any> {
    return this.http.get(`/assets/i18nCS/${lang}.json`);
  }
}

export function HttpLoaderFactory(http: HttpClient) {
  return new CustomTranslateLoader(http);
}

@NgModule({
  declarations: [
    AppComponent,
    LineupOptimizerComponent,
    OAuthSetupComponent,
    PlayersComponent,
    LoadStatusSidebarComponent,
    PlayerHistoryModalComponent
  ],
  imports: [
    BrowserModule,
    AppRoutingModule,
    HttpClientModule,
    FormsModule,
    TranslateModule.forRoot({
      loader: {
        provide: TranslateLoader,
        useFactory: HttpLoaderFactory,
        deps: [HttpClient]
      }
    })
  ],
  providers: [
    { provide: HTTP_INTERCEPTORS, useClass: CredentialsInterceptor, multi: true }
  ],
  bootstrap: [AppComponent]
})
export class AppModule { }
