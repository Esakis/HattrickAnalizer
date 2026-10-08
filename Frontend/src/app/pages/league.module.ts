import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { LeagueTableComponent } from '../components/league-table/league-table.component';

@NgModule({
  declarations: [LeagueTableComponent],
  imports: [CommonModule, FormsModule, TranslateModule, RouterModule.forChild([{ path: '', component: LeagueTableComponent }])]
})
export class LeaguePageModule {}
