import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { LineupOptimizerComponent } from './components/lineup-optimizer/lineup-optimizer.component';
import { AuthGuard } from './guards/auth.guard';

const routes: Routes = [
  { path: '', component: LineupOptimizerComponent, canActivate: [AuthGuard] },
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
