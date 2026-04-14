import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';

export type LoadState = 'idle' | 'loading' | 'success' | 'error';

export interface LoadItem {
  key: string;
  label: string;
  state: LoadState;
  message?: string;
  updatedAt?: Date;
}

@Injectable({ providedIn: 'root' })
export class LoadStatusService {
  private items = new Map<string, LoadItem>();
  private subject = new BehaviorSubject<LoadItem[]>([]);

  readonly items$: Observable<LoadItem[]> = this.subject.asObservable();

  register(key: string, label: string): void {
    if (!this.items.has(key)) {
      this.items.set(key, { key, label, state: 'idle' });
      this.emit();
    }
  }

  set(key: string, state: LoadState, message?: string): void {
    const current = this.items.get(key);
    if (!current) return;
    this.items.set(key, { ...current, state, message, updatedAt: new Date() });
    this.emit();
  }

  reset(): void {
    for (const [k, v] of this.items) {
      this.items.set(k, { ...v, state: 'idle', message: undefined });
    }
    this.emit();
  }

  private emit(): void {
    this.subject.next(Array.from(this.items.values()));
  }
}
