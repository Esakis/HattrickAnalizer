import { NgModule } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';
import { HttpClientModule } from '@angular/common/http';
import { FormsModule } from '@angular/forms';

import { AppRoutingModule } from './app-routing.module';
import { AppComponent } from './app.component';
import { LineupOptimizerComponent } from './components/lineup-optimizer/lineup-optimizer.component';
import { OAuthSetupComponent } from './components/oauth-setup/oauth-setup.component';
import { PlayersComponent } from './components/players/players.component';

@NgModule({
  declarations: [
    AppComponent,
    LineupOptimizerComponent,
    OAuthSetupComponent,
    PlayersComponent
  ],
  imports: [
    BrowserModule,
    AppRoutingModule,
    HttpClientModule,
    FormsModule
  ],
  providers: [],
  bootstrap: [AppComponent]
})
export class AppModule { }
