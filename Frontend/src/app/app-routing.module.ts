import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { LineupOptimizerComponent } from './components/lineup-optimizer/lineup-optimizer.component';
import { OAuthSetupComponent } from './components/oauth-setup/oauth-setup.component';
import { PlayersComponent } from './components/players/players.component';

const routes: Routes = [
  { path: '', component: LineupOptimizerComponent },
  { path: 'players', component: PlayersComponent },
  { path: 'oauth-setup', component: OAuthSetupComponent },
  { path: '**', redirectTo: '' }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
