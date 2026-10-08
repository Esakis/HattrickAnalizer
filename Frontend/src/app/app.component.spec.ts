import { NO_ERRORS_SCHEMA } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RouterTestingModule } from '@angular/router/testing';
import { TranslateModule } from '@ngx-translate/core';
import { of } from 'rxjs';
import { AppComponent } from './app.component';
import { DataCacheService } from './services/data-cache.service';
import { HattrickApiService } from './services/hattrick-api.service';
import { LoadStatusService } from './services/load-status.service';
import { TranslationService } from './services/translation.service';

describe('AppComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({
    imports: [RouterTestingModule, TranslateModule.forRoot()],
    declarations: [AppComponent],
    schemas: [NO_ERRORS_SCHEMA],
    providers: [
      { provide: TranslationService, useValue: { switchLanguage: jasmine.createSpy('switchLanguage'), getCurrentLanguage: () => 'en' } },
      { provide: HattrickApiService, useValue: { getCurrentOAuth: () => of({ authorized: false }) } },
      { provide: LoadStatusService, useValue: { register: jasmine.createSpy('register'), set: jasmine.createSpy('set') } },
      DataCacheService
    ]
  }));

  it('creates the app with its required service providers', () => {
    const fixture = TestBed.createComponent(AppComponent);
    expect(fixture.componentInstance).toBeTruthy();
  });
});
