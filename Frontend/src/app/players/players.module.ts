import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { PlayerHistoryModalComponent } from '../components/player-history-modal/player-history-modal.component';
import { PlayersComponent } from '../components/players/players.component';

@NgModule({
  declarations: [PlayersComponent, PlayerHistoryModalComponent],
  imports: [
    CommonModule,
    FormsModule,
    TranslateModule,
    RouterModule.forChild([{ path: '', component: PlayersComponent }])
  ]
})
export class PlayersModule {}
