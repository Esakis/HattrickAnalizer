import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { AuthGuard } from './guards/auth.guard';

const routes: Routes = [
  { path: '', canActivate: [AuthGuard], loadComponent: () => import('./components/week-dashboard/week-dashboard.component').then(m => m.WeekDashboardComponent) },
  { path: 'optimizer', canActivate: [AuthGuard], loadChildren: () => import('./pages/optimizer.module').then(m => m.OptimizerPageModule) },
  { path: 'review', canActivate: [AuthGuard], loadComponent: () => import('./components/match-review/match-review.component').then(m => m.MatchReviewComponent) },
  { path: 'players', canActivate: [AuthGuard], loadChildren: () => import('./players/players.module').then(m => m.PlayersModule) },
  { path: 'league', canActivate: [AuthGuard], loadChildren: () => import('./pages/league.module').then(m => m.LeaguePageModule) },
  { path: 'training', canActivate: [AuthGuard], loadChildren: () => import('./pages/training.module').then(m => m.TrainingPageModule) },
  { path: 'scout', canActivate: [AuthGuard], loadChildren: () => import('./pages/scout.module').then(m => m.ScoutPageModule) },
  { path: 'oauth-setup', loadChildren: () => import('./pages/oauth.module').then(m => m.OAuthPageModule) },
  { path: '**', redirectTo: '' }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
