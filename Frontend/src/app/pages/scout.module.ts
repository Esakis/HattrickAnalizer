import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { ScoutViewComponent } from '../components/scout-view/scout-view.component';

@NgModule({
  declarations: [ScoutViewComponent],
  imports: [CommonModule, FormsModule, TranslateModule, RouterModule.forChild([{ path: '', component: ScoutViewComponent }])]
})
export class ScoutPageModule {}
