import { Injectable } from '@angular/core';
import { HttpErrorResponse, HttpEvent, HttpHandler, HttpInterceptor, HttpRequest } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { DataCacheService } from '../services/data-cache.service';

// 401 z backendu oznacza brak/wygaśnięcie sesji OAuth — czyścimy stan
// i kierujemy do ekranu logowania zamiast pokazywać puste/stare dane.
@Injectable()
export class ErrorInterceptor implements HttpInterceptor {
  constructor(private router: Router, private cache: DataCacheService) {}

  intercept(req: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    return next.handle(req).pipe(
      catchError((err: HttpErrorResponse) => {
        if (err.status === 401 && !req.url.includes('/oauth/')) {
          this.cache.clear();
          this.router.navigateByUrl('/oauth-setup');
        }
        return throwError(() => err);
      })
    );
  }
}
