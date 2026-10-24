import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { TrainingViewComponent } from '../components/training-view/training-view.component';

@NgModule({
  declarations: [TrainingViewComponent],
  imports: [CommonModule, FormsModule, TranslateModule, RouterModule.forChild([{ path: '', component: TrainingViewComponent }])]
})
export class TrainingPageModule {}
