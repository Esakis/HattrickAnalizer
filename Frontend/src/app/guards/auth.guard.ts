import { Injectable } from '@angular/core';
import { CanActivate, Router, UrlTree } from '@angular/router';
import { Observable, of } from 'rxjs';
import { catchError, map, tap } from 'rxjs/operators';
import { HattrickApiService } from '../services/hattrick-api.service';
import { DataCacheService } from '../services/data-cache.service';

// Chroni strony wymagajace danych druzyny: bez autoryzacji CHPP
// przekierowuje na /oauth-setup zamiast pokazywac pusta strone.
@Injectable({ providedIn: 'root' })
export class AuthGuard implements CanActivate {
  constructor(
    private api: HattrickApiService,
    private cache: DataCacheService,
    private router: Router
  ) {}

  canActivate(): Observable<boolean | UrlTree> {
    const cached = this.cache.auth$.value;
    if (cached.authorized) {
      return of(true);
    }
    return this.api.getCurrentOAuth().pipe(
      tap(auth => this.cache.auth$.next(auth)),
      map(auth => auth.authorized ? true : this.router.parseUrl('/oauth-setup')),
      catchError(() => of(this.router.parseUrl('/oauth-setup')))
    );
  }
}
