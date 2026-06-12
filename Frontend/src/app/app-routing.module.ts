import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { LineupOptimizerComponent } from './components/lineup-optimizer/lineup-optimizer.component';
import { OAuthSetupComponent } from './components/oauth-setup/oauth-setup.component';
import { PlayersComponent } from './components/players/players.component';
import { AuthGuard } from './guards/auth.guard';

const routes: Routes = [
  { path: '', component: LineupOptimizerComponent, canActivate: [AuthGuard] },
  { path: 'players', component: PlayersComponent, canActivate: [AuthGuard] },
  { path: 'oauth-setup', component: OAuthSetupComponent },
  { path: '**', redirectTo: '' }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
